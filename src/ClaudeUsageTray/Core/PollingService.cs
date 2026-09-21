using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Core;

// =============================================================================
//  ポーリング
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. 行儀よく叩く。これが最優先。
//     Claude Code 本体は同じエンドポイントを内部で 60 秒スロットルしている
//     （「同一マシンの複数ウィンドウは直近 1 分の結果を共有する」とチェンジ
//     ログにも明記がある）。本体より速く叩くのは筋が悪く、429 や WAF ブロック
//     を招けば本体の認証まで巻き添えになりうる。
//     → 間隔の下限 60 秒は設定でも破れないようハードクランプする。
//     → 同時リクエストも投げない（UsageEndpointClient 側で直列化）。
//     → ±10% のジッタを入れ、複数インスタンスや復帰時に時刻が揃わないようにする。
//
//  2. 認証は「本体に追従する」だけ。自分では何もしない。
//     アクセストークンの寿命は約 4.7 時間なので、常駐していれば必ず期限切れに
//     遭う。だが自前でリフレッシュはしない（Credentials.cs の注記参照）。
//     毎回 .credentials.json を読み直し、本体が書き戻した新しいトークンを拾う。
//
//  3. 失敗しても前の値を捨てない。
//     ネットワークが切れた瞬間に表示が空になると、常駐パネルとしては
//     「壊れた」ように見える。状態だけ差し替えて数値は保持し、
//     古さは UI 側で表現する。
//
//  【未実装（Phase 2 で足す）】
//     ・401 後の ShortWatch（.credentials.json の mtime を短間隔で見張る）
//     ・指数バックオフと Retry-After の尊重
//     ・resets_at を過ぎた直後の前倒し取得
//     ・Claude Code が 1 つも起動していないときの間隔引き延ばし
// =============================================================================
internal sealed class PollingService : IDisposable
{
    public const int MinIntervalSeconds = 60;

    private readonly UsageEndpointClient _client;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _jitter = new();
    private Task? _loop;

    /// <summary>
    /// ★ 取得処理を 1 本に直列化する。
    ///
    /// メニューの「今すぐ更新」は定期ループとは独立した Task で走るため、
    /// 押した瞬間に定期取得が実行中だと 2 本が並行する。すると
    ///   ・CredentialsReader の静的キャッシュに無同期で読み書きが起きる
    ///   ・先に始まった古い結果が、後から来た新しい結果を上書きしうる
    ///     （表示が一瞬巻き戻る）
    /// HTTP 自体は UsageEndpointClient 側でも直列化しているが、
    /// 認証情報の読み取りと状態の発行はその外側なので、ここで囲う必要がある。
    /// </summary>
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    public event Action<AppState>? StateChanged;

    public AppState Current { get; private set; } = AppState.Initial;

    public PollingService(Uri endpoint, int intervalSeconds)
    {
        _client = new UsageEndpointClient(endpoint);
        _interval = TimeSpan.FromSeconds(Math.Max(MinIntervalSeconds, intervalSeconds));
    }

    /// <param name="initialDelay">
    /// 自動起動から立ち上がった直後はログオン処理で I/O が混んでおり、
    /// Claude Code 本体もまだ起動していないことが多い。少し待ってから最初の 1 回を投げる。
    /// </param>
    public void Start(TimeSpan initialDelay = default) => _loop = Task.Run(() => RunAsync(initialDelay));

    /// <summary>メニューの「今すぐ更新」用。</summary>
    public void RequestRefresh() => _ = Task.Run(() => PollOnceAsync(_cts.Token));

    private async Task RunAsync(TimeSpan initialDelay)
    {
        if (initialDelay > TimeSpan.Zero)
        {
            try { await Task.Delay(initialDelay, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        while (!_cts.IsCancellationRequested)
        {
            await PollOnceAsync(_cts.Token).ConfigureAwait(false);

            try
            {
                await Task.Delay(NextDelay(), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan NextDelay()
    {
        // ±10% のジッタ。複数インスタンスや PC 復帰時に時刻が揃うのを避ける。
        double factor = 0.9 + _jitter.NextDouble() * 0.2;
        return TimeSpan.FromSeconds(_interval.TotalSeconds * factor);
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // ★ 毎回読み直す。本体がトークンを更新して書き戻したら自動で追従する。
            var creds = CredentialsReader.Read();

            var usage = await _client.FetchAsync(creds.AccessToken, ct).ConfigureAwait(false);

            Publish(usage.RateLimitsAvailable
                ? new AppState(usage, FetchStatus.Ok, null)
                : new AppState(usage, FetchStatus.NotAvailable, "プランリミットの情報がありません。"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 終了中
        }
        catch (UsageUnauthorizedException)
        {
            Log.Warn("401 を受け取りました。Claude Code 本体がトークンを更新するのを待ちます。");
            Publish(Current with
            {
                Status = FetchStatus.AuthRequired,
                StatusDetail = "Claude Code を起動してサインインし直してください。",
            });
        }
        catch (CredentialsUnavailableException ex)
        {
            Publish(Current with { Status = FetchStatus.AuthRequired, StatusDetail = ex.Message });
        }
        catch (UsageHttpException ex)
        {
            Log.Error($"使用量の取得に失敗しました ({ex.Message})");
            Publish(Current with { Status = FetchStatus.Offline, StatusDetail = ex.Message });
        }
        catch (Exception ex)
        {
            Log.Error("使用量の取得に失敗しました", ex);
            Publish(Current with
            {
                Status = FetchStatus.Offline,
                StatusDetail = $"{ex.GetType().Name}: {Log.ShortMessage(ex)}",
            });
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private void Publish(AppState state)
    {
        Current = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* 終了時の例外は無視 */ }
        _cts.Dispose();
        _pollGate.Dispose();
    }
}
