using System.Net;
using System.Text.Json;
using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Core;

// =============================================================================
//  使用量エンドポイントの呼び出し
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  取得元は GET https://api.anthropic.com/api/oauth/usage。
//  これは公開ドキュメントに無い "内部" エンドポイントで、Claude Code 本体の
//  バイナリから見つけたもの。非公開 API に依存するのは本来避けたいが、
//  ほかに手が無く、かつ壊れにくいと判断して採用している。
//
//  なぜ他の手段ではないのか:
//    ・ローカルの JSONL 解析 … トークン数は出せるが「リミットの分母」が
//      非公開なので消費率 % は原理的に出せない（ccusage 等が P90 推定に
//      頼っているのはこのため）。本アプリの主目的は % なので不可
//    ・Admin API            … 組織の API 従量課金の集計であって、
//      サブスクの 5時間 / 週次リミットとは無関係。レスポンスにレート制限
//      ウィンドウの概念自体が無い
//    ・claude.ai の Web API … sessionKey Cookie が要り、Cloudflare に
//      弾かれる報告が多い。同じデータならこちらのほうが安全
//    ・statusLine フック     … 同じ rate_limits が取れるが、Claude Code が
//      起動しているときしか動かない。常駐アプリの主データ源にはできない
//      （撤退時のフォールバックとしては有効）
//
//  なぜ壊れにくいと考えるか:
//    /usage スラッシュコマンド・VS Code の usage パネル・statusLine の
//    rate_limits が すべてこのエンドポイントに依存している。Anthropic 側にも
//    壊しにくい強い動機がある。
//
//  それでも壊れる前提で、次の保険を入れてある:
//    ・未知フィールドは無視する（サーバー側スキーマは passthrough）
//    ・成功レスポンスの生 JSON を常に cache\last-usage.json へ残す。
//      スキーマが変わったとき diff を取れる唯一の手がかり
//    ・パース失敗時は生ボディを last-error-body.txt へ残す
// =============================================================================

internal sealed class UsageUnauthorizedException() : Exception("401 Unauthorized");

internal sealed class UsageHttpException(HttpStatusCode status, TimeSpan? retryAfter)
    : Exception($"HTTP {(int)status} {status}")
{
    public HttpStatusCode Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class UsageEndpointClient(Uri endpoint)
{
    public const string DefaultEndpoint = "https://api.anthropic.com/api/oauth/usage";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    })
    {
        // タイムアウトは CTS 側で制御する。HttpClient.Timeout だと TaskCanceledException に
        // なってユーザーキャンセルと区別できない。
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>同時リクエストを絶対に投げない（429 対策）。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// ★ 最後に実際に送信した時刻。下限間隔の強制に使う。
    ///
    /// 「60 秒未満では叩かない」は README にも CLAUDE.md にも不変条件として
    /// 書いてあるが、**守っていたのは PollingService の待ち時間計算だけ**だった。
    /// メニューの「今すぐ更新」はそこを通らないので、連打すれば連打しただけ
    /// 飛んでいた（＝不変条件が呼び出し側の作法に依存していた）。
    ///
    /// Log.Redact と同じ考え方で、**唯一の出口であるここで強制する**。
    /// こうすれば誰がどう呼んでも破れない。
    /// </summary>
    private static DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;

    private static readonly TimeSpan MinSendInterval = TimeSpan.FromSeconds(60);

    public async Task<ServerUsage> FetchAsync(string accessToken, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // ★ 下限に満たないうちは、送らずに待つ。
            //   定期ループは元から 60 秒以上空けているのでここに入らない。
            //   入るのは「今すぐ更新」を早く押したときだけ。
            var since = DateTimeOffset.UtcNow - _lastSentAt;
            if (since < MinSendInterval)
            {
                var wait = MinSendInterval - since;
                Log.Info($"前回の送信から {since.TotalSeconds:F0} 秒しか経っていないので "
                       + $"{wait.TotalSeconds:F0} 秒待ちます。");
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);

            // ★ DefaultRequestHeaders には入れない。常駐オブジェクトにトークンを残さないため。
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation(
                "User-Agent", $"claude-cli/{ClaudeVersion.Detect()} (external, cli)");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            _lastSentAt = DateTimeOffset.UtcNow;

            using var res = await Http.SendAsync(
                req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

            if (res.StatusCode == HttpStatusCode.Unauthorized)
                throw new UsageUnauthorizedException();

            if (!res.IsSuccessStatusCode)
                throw new UsageHttpException(res.StatusCode, ParseRetryAfter(res));

            string body = await res.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Parse(body);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// ★ Retry-After には 2 つの形式がある。**両方読まないと「尊重した」ことにならない。**
    ///
    ///   Retry-After: 120                              → 秒数。HttpClient は Delta に入れる
    ///   Retry-After: Wed, 23 Sep 2026 20:00:00 GMT    → 絶対時刻。Date に入り Delta は null
    ///
    /// 当初は Delta しか読んでおらず、日付形式が来ると**黙って無視**していた。
    /// 「Retry-After を尊重する」と書きながら半分しか見ていない状態で、
    /// これは何も書かないより有害（CLAUDE.md の「対策したつもりを文書化しない」）。
    /// </summary>
    private static TimeSpan? ParseRetryAfter(HttpResponseMessage res)
    {
        var header = res.Headers.RetryAfter;
        if (header is null) return null;

        if (header.Delta is { } delta)
            return delta > TimeSpan.Zero ? delta : null;

        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }

    internal static ServerUsage Parse(string body)
    {
        UsageResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<UsageResponseDto>(body);
        }
        catch (JsonException ex)
        {
            SaveErrorBody(body);
            throw new InvalidOperationException("使用量レスポンスの JSON を解析できませんでした。", ex);
        }

        var rows = (dto?.Limits ?? [])
            .Select(LimitRow.From)
            .OfType<LimitRow>()
            .ToList();

        // 生 JSON は常に残す。スキーマが変わったとき diff が取れる唯一の手がかり。
        SaveLastUsage(body);

        return new ServerUsage(DateTimeOffset.UtcNow, rows);
    }

    private static void SaveLastUsage(string body) =>
        WriteAtomic(Path.Combine(AppPaths.CacheDir, "last-usage.json"), body);

    private static void SaveErrorBody(string body) =>
        // レスポンスボディのみ。リクエストヘッダは絶対に保存しない（トークンが載る）。
        WriteAtomic(Path.Combine(AppPaths.CacheDir, "last-error-body.txt"), body);

    private static void WriteAtomic(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"キャッシュを書けませんでした: {Path.GetFileName(path)} ({ex.GetType().Name})");
        }
    }
}
