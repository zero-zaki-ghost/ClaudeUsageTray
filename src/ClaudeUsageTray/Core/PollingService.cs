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
//  3. ★ 失効が分かっているトークンは送らない。
//     .credentials.json には expiresAt がある。**失効は送る前に分かる。**
//     「送ってみて 401 で確かめる」必要がそもそも無い。
//
//     これを怠って実害を出した（2026-09-22）。PC 再起動の直後はトークンが
//     失効しており、Claude Code 本体が起動するまで誰も更新しない。そこへ
//     2 分おきに失効トークンを投げ続けた結果、401 が 3 回続いたところで
//     サーバーが 429 に切り替え、本体を起動するまで 12 分間エラーになった。
//     **429 はサーバー都合ではなく、こちらが作っていた。**
//
//  4. 失敗しても前の値を捨てない。そして案内も捨てない。
//     ネットワークが切れた瞬間に表示が空になると、常駐パネルとしては
//     「壊れた」ように見える。状態だけ差し替えて数値は保持し、
//     古さは UI 側で表現する。
//
//     加えて **AuthRequired を Offline で上書きしない**。前者は
//     「Claude Code を起動すれば直る」という行動可能な案内で、後者は
//     「待つしかない」。429 で後者に落とすと、直せる状況なのに
//     直せないと伝えることになる。
//
//  【未実装（Phase 2 の残り）】
//     ・401 後の ShortWatch（.credentials.json の mtime を短間隔で見張る）
//       → 3 の「送らない」で 429 の実害は消えたので、優先度は下がった
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
    /// 連続失敗の回数。成功で 0 に戻す。バックオフの段数に使う。
    /// 401 も数える点が設計書 8-4 と異なる（下の OnFailure の注記を参照）。
    /// </summary>
    private int _failures;

    /// <summary>
    /// バックオフから戻るために必要な連続成功の回数。設計書 8-4 の「成功 3 回で戻す」。
    ///
    /// 1 回の成功で戻すと、サーバーがまだ締めている最中に元のペースへ復帰してしまう。
    /// 回復したことを 3 回確認してから戻す。
    /// </summary>
    private const int SuccessesToRecover = 3;

    private int _successStreak;

    /// <summary>サーバーが Retry-After で指定してきた待ち時間。1 回使ったら捨てる。</summary>
    private TimeSpan? _retryAfter;

    /// <summary>
    /// この周期で実際に送信したか。
    ///
    /// 失効でスキップした周期にバックオフを適用してしまうと、**1 通も送っていないのに
    /// 待ち時間が 600 秒**になり、本体がトークンを更新しても気づくのが 10 分後になる。
    /// 送っていないならサーバーへの遠慮は要らないので、通常間隔で見に行く。
    /// </summary>
    private bool _sentThisCycle;

    /// <summary>失効ログを毎周期出さないための抑制。成功で解除する。</summary>
    private bool _expiredLogged;

    /// <summary>
    /// 失効と判定していても、たまに 1 回だけ送ってみる時刻の基準。
    ///
    /// expiresAt はサーバー由来の絶対時刻なので、**端末の時計が大きくずれていると
    /// 失効していないトークンを失効と誤判定しうる**。その場合に「送らない」だけだと
    /// アプリが永久に沈黙し、しかも理由が画面に出ない。逃げ道として残してある。
    /// 起動直後は送らせたくないので、最初の 1 回は 30 分後になるよう now で初期化する。
    /// </summary>
    private DateTimeOffset _lastExpiredProbe = DateTimeOffset.UtcNow;

    private static readonly TimeSpan ExpiredProbeInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 5xx / タイムアウト / 429（Retry-After 無し）の待ち時間。
    /// 通常間隔より短い段は意味が無いので、実際には通常間隔との大きいほうを採る。
    /// </summary>
    private static readonly TimeSpan[] BackoffLadder =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(240),
        TimeSpan.FromSeconds(480),
        TimeSpan.FromSeconds(600),
    ];

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

    /// <summary>
    /// メニューの「今すぐ更新」用。
    ///
    /// 既に取得中なら何もしない。連打しても待ち行列が伸びないようにするため。
    /// 送信間隔の下限そのものは UsageEndpointClient 側で強制しているので、
    /// ここを抜けても 60 秒より速くは飛ばない。
    /// </summary>
    public void RequestRefresh()
    {
        if (_pollGate.CurrentCount == 0)
        {
            Log.Info("取得中なので「今すぐ更新」は無視しました。");
            return;
        }

        _ = Task.Run(() => PollOnceAsync(_cts.Token));
    }

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

    /// <summary>
    /// 次の待ち時間。**短くする方向には決して働かない。**
    /// 下限 60 秒（MinIntervalSeconds）はここでも必ず通す。
    /// </summary>
    private TimeSpan NextDelay()
    {
        // ⓪ 今回 1 通も送っていないなら、サーバーへの遠慮は不要。
        //    バックオフを持ち越すと、復帰の検知だけが無駄に遅くなる。
        //    _retryAfter / _failures は次に実際に送るときのために残しておく。
        if (!_sentThisCycle)
            return Jitter(_interval, 0.1);

        // ① サーバーが明示的に待てと言ってきたら、それを最優先する。
        if (_retryAfter is { } asked)
        {
            _retryAfter = null;

            // 指定が下限より短くても、こちらの下限は下回らない。
            var wait = asked < MinInterval ? MinInterval : asked;

            // ジッタは「増やす」側にだけ振る。言われた時間より早く叩かないため。
            Log.Warn($"Retry-After に従って {wait.TotalSeconds:F0} 秒待ちます。");
            return TimeSpan.FromSeconds(wait.TotalSeconds * (1.0 + _jitter.NextDouble() * 0.2));
        }

        // ② 連続失敗中は段階的に延ばす。通常間隔より短い段は使わない。
        if (_failures > 0)
        {
            var step = BackoffLadder[Math.Min(_failures - 1, BackoffLadder.Length - 1)];
            var wait = step > _interval ? step : _interval;
            return Jitter(wait, 0.2);
        }

        // ③ 通常。複数インスタンスや PC 復帰時に時刻が揃うのを避ける。
        return Jitter(_interval, 0.1);
    }

    private TimeSpan Jitter(TimeSpan span, double ratio)
    {
        double factor = 1.0 - ratio + _jitter.NextDouble() * ratio * 2.0;
        return TimeSpan.FromSeconds(span.TotalSeconds * factor);
    }

    private static TimeSpan MinInterval => TimeSpan.FromSeconds(MinIntervalSeconds);

    /// <summary>
    /// 成功を記録する。**1 回の成功では元のペースに戻さない。**
    /// 設計書 8-4 のとおり、連続 3 回の成功を確認してからバックオフを解除する。
    /// </summary>
    private void OnSuccess()
    {
        _expiredLogged = false;
        _retryAfter = null;

        if (_failures == 0) return;

        if (++_successStreak >= SuccessesToRecover)
        {
            Log.Info($"{SuccessesToRecover} 回連続で成功したので通常の間隔に戻します。");
            _failures = 0;
            _successStreak = 0;
        }
        // まだ足りないなら _failures は据え置く（＝間隔を伸ばしたまま様子を見る）。
    }

    /// <summary>
    /// 失敗を記録する。
    ///
    /// ⚠ 設計書 8-4 は「401 はバックオフに乗せない」としていたが、**乗せる**ことにした。
    ///   失効トークンは送らなくなった（ShouldSkipExpired）ので、それでもなお 401 が
    ///   返るのは「失効ではない理由で拒否されている」＝ 待っても直らない可能性が高い。
    ///   2026-09-22 に踏んだのは、まさに 401 を繰り返して 429 に昇格させた事故だった。
    /// </summary>
    private void OnFailure(TimeSpan? retryAfter)
    {
        _successStreak = 0;
        if (_failures < int.MaxValue) _failures++;
        if (retryAfter is { } ra && ra > TimeSpan.Zero) _retryAfter = ra;
    }

    /// <summary>
    /// 一時的な通信失敗を状態に反映する。
    ///
    /// ★ AuthRequired を Offline で上書きしない。
    ///   「Claude Code を起動すれば直る」という行動可能な案内が、
    ///   429 のせいで「接続できません」に化けるのを防ぐ（実機で踏んだ）。
    /// </summary>
    private AppState TransientFailure(string detail) =>
        Current.Status == FetchStatus.AuthRequired
            ? Current
            : Current with { Status = FetchStatus.Offline, StatusDetail = detail };

    /// <summary>
    /// トークンが単に失効しているとき（PC の電源が落ちていた等）の案内。
    ///
    /// ★ ここで「サインインし直してください」と書いてはいけない。
    ///   失効しただけなら Claude Code を起動すれば本体が自動で更新する。
    ///   再サインインは不要な上に、ユーザーにとってはるかに重い操作であり、
    ///   「何か壊した」と思わせる。実際にこの文面が出ていた（再起動直後は
    ///   数値がまだ無いので StatusDetail がそのままパネルに出る）。
    /// </summary>
    private const string ExpiredGuidance = "Claude Code を起動すると自動で復帰します。";

    /// <summary>
    /// 失効していないのに拒否されたときの案内。こちらは再サインインが要りうる。
    /// </summary>
    private const string RejectedGuidance =
        "Claude Code を起動してください。直らなければサインインし直してください。";

    /// <summary>
    /// ★ 失効が分かっているトークンを送らないための判定。
    ///   これが 429 の根治にあたる。詳しくはファイル冒頭の 3 を参照。
    /// </summary>
    private bool ShouldSkipExpired(Credentials creds)
    {
        if (!creds.IsExpired) return false;

        // 時計のずれで永久に沈黙しないための逃げ道（_lastExpiredProbe の注記を参照）。
        var now = DateTimeOffset.UtcNow;
        if (now - _lastExpiredProbe >= ExpiredProbeInterval)
        {
            _lastExpiredProbe = now;
            Log.Warn("失効と判定しているが、端末の時計がずれている可能性もあるので 1 回だけ試します。");
            return false;
        }

        if (!_expiredLogged)
        {
            _expiredLogged = true;
            Log.Warn($"トークンが {-creds.Remaining.TotalMinutes:F0} 分前に失効しています。"
                   + "Claude Code 本体が更新するまで送信を見送ります。");
        }

        return true;
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

        _sentThisCycle = false;

        try
        {
            // ★ 毎回読み直す。本体がトークンを更新して書き戻したら自動で追従する。
            var creds = CredentialsReader.Read();

            // ★ 失効が分かっているなら送らない。401 を確かめに行く必要は無い。
            if (ShouldSkipExpired(creds))
            {
                Publish(Current with
                {
                    Status = FetchStatus.AuthRequired,
                    StatusDetail = ExpiredGuidance,
                });
                return;
            }

            _sentThisCycle = true;
            var usage = await _client.FetchAsync(creds.AccessToken, ct).ConfigureAwait(false);

            OnSuccess();

            Publish(usage.RateLimitsAvailable
                ? new AppState(usage, FetchStatus.Ok, null)
                : new AppState(usage, FetchStatus.NotAvailable, "プランリミットの情報がありません。"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 終了中。失敗として数えない。
        }
        catch (UsageUnauthorizedException)
        {
            // 失効チェックを通ったのに 401 ＝ 失効以外の理由で拒否されている。
            // 待っても直るとは限らないので、バックオフに乗せて叩く回数を減らす。
            OnFailure(null);
            Log.Warn("401 を受け取りました。Claude Code 本体がトークンを更新するのを待ちます。");
            Publish(Current with
            {
                Status = FetchStatus.AuthRequired,
                StatusDetail = RejectedGuidance,
            });
        }
        catch (CredentialsUnavailableException ex)
        {
            // 通信していないのでバックオフの対象外。ファイルが戻れば次の周期で直る。
            Publish(Current with { Status = FetchStatus.AuthRequired, StatusDetail = ex.Message });
        }
        catch (UsageHttpException ex)
        {
            OnFailure(ex.RetryAfter);
            Log.Error($"使用量の取得に失敗しました ({ex.Message}, 連続 {_failures} 回目)");
            Publish(TransientFailure(ex.Message));
        }
        catch (Exception ex)
        {
            OnFailure(null);
            Log.Error($"使用量の取得に失敗しました（連続 {_failures} 回目）", ex);
            Publish(TransientFailure($"{ex.GetType().Name}: {Log.ShortMessage(ex)}"));
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
