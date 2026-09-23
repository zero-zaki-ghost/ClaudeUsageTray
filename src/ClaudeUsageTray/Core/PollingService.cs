using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Core;

// =============================================================================
//  ポーリング
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. ★ 取得の入口は 1 本だけにする。
//     以前は定期ループと「今すぐ更新」が別々の Task で走り、セマフォで
//     衝突を防いでいた。衝突は防げても、
//       ・手動の成功がループのバックオフを解除してしまう
//       ・手動が下限間隔の判断を素通りする
//     といった「片方にだけ効く」穴が次々に出た。
//
//     いまは **ループが 1 本だけ**で、外からの要求は「待機を打ち切る」だけ。
//     取得は必ず同じ経路を通るので、片方にだけ効く状態を作れない。
//
//  2. 役割を 3 つに割った。
//       PollSchedule … いつ投げるか（純粋。時計も通信も要らずテストできる）
//       ISendGuard   … 投げてよいか（止める理由と案内文をセットで返す）
//       ここ          … 上の 2 つに従って投げ、結果を状態に変換する
//
//     混ざっていたときは、429 を実機で出すまで誰も間隔の誤りに気づけなかった。
//     間隔の判断だけを単体で動かせる形にしておくのが再発防止になる。
//
//  3. 認証は「本体に追従する」だけ。自分では何もしない。
//     アクセストークンの寿命は約 4.7 時間なので、常駐していれば必ず失効に遭う。
//     だが自前でリフレッシュはしない（Credentials.cs の注記）。
//     毎回 .credentials.json を読み直し、本体が書き戻した新しいトークンを拾う。
//
//  4. 失敗しても、前の値も案内も捨てない。
//     ネットワークが切れた瞬間に表示が空になると「壊れた」ように見える。
//     状態だけ差し替えて数値は保持する。
//     加えて **AuthRequired を Offline で上書きしない**。前者は
//     「Claude Code を起動すれば直る」という行動可能な案内で、後者は
//     「待つしかない」。429 で後者に落とすと、直せる状況なのに直せないと伝わる。
//
//  5. OS のイベントは自分で購読しない。
//     ネットワーク復帰やスリープ復帰を拾うには Platform 層が要るが、
//     ここがそれを直接掴むと Core が Platform に依存してしまい、
//     「Core は UI にも OS にも依存しない＝テストから直接叩ける」という
//     規約が崩れる。**受け口（Wake）だけ用意して、配線は合成の場所に任せる。**
//
//  【未実装】
//     ・本体プロセスの検出（懸念 8）… ISendGuard を 1 つ足すだけで入る
//     ・resets_at を過ぎた直後の前倒し取得
// =============================================================================
internal sealed class PollingService : IDisposable
{
    /// <summary>これより速くは絶対に叩かない。実際の強制は UsageEndpointClient 側。</summary>
    public const int MinIntervalSeconds = 60;

    private readonly UsageEndpointClient _client;
    private readonly PollSchedule _schedule;
    private readonly IReadOnlyList<ISendGuard> _guards;

    private readonly CancellationTokenSource _stop = new();

    /// <summary>いま走っている待機。起床シグナルはこれを打ち切る。</summary>
    private CancellationTokenSource? _sleep;

    /// <summary>待機に入る前に届いた起床要求。次の待機で消費する（Wake の注記を参照）。</summary>
    private bool _wakePending;

    private readonly Lock _sleepGate = new();
    private Task? _loop;

    public event Action<AppState>? StateChanged;

    public AppState Current { get; private set; } = AppState.Initial;

    public PollingService(Uri endpoint, int intervalSeconds)
    {
        _client = new UsageEndpointClient(endpoint);
        _schedule = new PollSchedule(
            normalInterval: TimeSpan.FromSeconds(Math.Max(MinIntervalSeconds, intervalSeconds)),
            minInterval: TimeSpan.FromSeconds(MinIntervalSeconds));

        _guards = [new ExpiredTokenGuard()];
    }

    /// <param name="initialDelay">
    /// 自動起動から立ち上がった直後はログオン処理で I/O が混んでおり、
    /// Claude Code 本体もまだ起動していないことが多い。少し待ってから最初の 1 回を投げる。
    /// </param>
    public void Start(TimeSpan initialDelay = default) => _loop = Task.Run(() => RunAsync(initialDelay));

    /// <summary>メニューの「今すぐ更新」用。</summary>
    public void RequestRefresh() => Wake("手動");

    /// <summary>
    /// 待機を打ち切って、すぐ次の周期へ進ませる。取得そのものは必ずループが行う。
    ///
    /// ネットワーク復帰・スリープ復帰・ロック解除を渡すのは合成の場所の役目
    /// （<c>Platform.WakeSignals</c> を購読して、ここへ流す）。
    /// **起こすだけで、送るかどうかの判断には一切使わない。**
    /// 誤検出しても「余計に 1 回取りに行く」で済ませるため。
    /// </summary>
    public void Wake(string reason)
    {
        lock (_sleepGate)
        {
            // ★ 取りこぼし防止。
            //   起床が来るのは「待機中」とは限らない。取得中かもしれないし、
            //   待機に入る直前（CTS を作ってから _sleep に入れるまで）かもしれない。
            //   Cancel だけに頼るとその隙間で消える。消えた起床は、
            //   ネットワーク復帰を最大 30 分取りこぼすということで、
            //   **起床シグナルを入れた目的そのものを損なう。**
            //   要求を旗で残し、次に待機へ入るときに消費する。
            _wakePending = true;

            try { _sleep?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        Log.Info($"起床: {reason}");
    }

    private async Task RunAsync(TimeSpan initialDelay)
    {
        if (initialDelay > TimeSpan.Zero && !await SleepAsync(new NextPoll(initialDelay, true)))
            return;

        while (!_stop.IsCancellationRequested)
        {
            var next = await PollOnceAsync().ConfigureAwait(false);
            if (!await SleepAsync(next).ConfigureAwait(false)) return;
        }
    }

    /// <summary>1 回分の取得。次の待ち方を返す。</summary>
    private async Task<NextPoll> PollOnceAsync()
    {
        var ct = _stop.Token;

        try
        {
            // ★ 毎回読み直す。本体がトークンを更新して書き戻したら自動で追従する。
            var creds = CredentialsReader.Read();

            foreach (var guard in _guards)
            {
                var decision = guard.Evaluate(creds);
                if (decision.ShouldSend) continue;

                Publish(Current with { Status = decision.Status, StatusDetail = decision.Guidance });
                return _schedule.AfterSkip();
            }

            var usage = await _client.FetchAsync(creds.AccessToken, ct).ConfigureAwait(false);

            if (_schedule.RecordSuccess())
                Log.Info("連続して成功したので通常の間隔に戻しました。");

            Publish(usage.RateLimitsAvailable
                ? new AppState(usage, FetchStatus.Ok, null)
                : new AppState(usage, FetchStatus.NotAvailable, "プランリミットの情報がありません。"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 終了中。失敗として数えない。
            return new NextPoll(TimeSpan.Zero, true);
        }
        catch (UsageUnauthorizedException)
        {
            // Guard を通ったのに 401 ＝ 失効以外の理由で拒否されている。
            // 待っても直るとは限らないので、バックオフに乗せて回数を減らす。
            _schedule.RecordFailure();
            Log.Warn("401 を受け取りました。Claude Code 本体がトークンを更新するのを待ちます。");
            Publish(Current with
            {
                Status = FetchStatus.AuthRequired,
                StatusDetail = "Claude Code を起動してください。直らなければサインインし直してください。",
            });
        }
        catch (CredentialsUnavailableException ex)
        {
            // 通信していないのでバックオフの対象外。ファイルが戻れば次の周期で直る。
            Publish(Current with { Status = FetchStatus.AuthRequired, StatusDetail = ex.Message });
            return _schedule.AfterSkip();
        }
        catch (UsageHttpException ex)
        {
            _schedule.RecordFailure(ex.RetryAfter);
            Log.Error($"使用量の取得に失敗しました ({ex.Message}, 連続 {_schedule.ConsecutiveFailures} 回目)");
            Publish(TransientFailure(ex.Message));
        }
        catch (Exception ex)
        {
            _schedule.RecordFailure();
            Log.Error($"使用量の取得に失敗しました（連続 {_schedule.ConsecutiveFailures} 回目）", ex);
            Publish(TransientFailure($"{ex.GetType().Name}: {Log.ShortMessage(ex)}"));
        }

        return _schedule.AfterSend();
    }

    /// <summary>
    /// 次の周期まで待つ。戻り値 false は「終了しろ」。
    ///
    /// 割り込み可の待機は、起床シグナルで打ち切られる。
    /// Retry-After が効いている周期だけは割り込ませない
    /// （サーバーが待てと言っているものを、こちらの都合で早めない）。
    /// </summary>
    private async Task<bool> SleepAsync(NextPoll next)
    {
        if (_stop.IsCancellationRequested) return false;
        if (next.Delay <= TimeSpan.Zero) return !_stop.IsCancellationRequested;

        CancellationTokenSource? sleep = null;

        try
        {
            if (next.Interruptible)
            {
                sleep = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);

                lock (_sleepGate)
                {
                    // 待機へ入る前に届いていた起床要求は、待たずに消費する。
                    if (_wakePending) return !_stop.IsCancellationRequested;

                    _sleep = sleep;
                }
            }

            await Task.Delay(next.Delay, sleep?.Token ?? _stop.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 終了なら抜ける。起床なら、すぐ次の周期へ進む。
            return !_stop.IsCancellationRequested;
        }
        finally
        {
            lock (_sleepGate)
            {
                // ★ ここを抜けた直後に必ず 1 回取得するので、
                //   どの経路で抜けても起床要求は果たされたことになる。
                //   消さずに残すと、1 回の起床で 2 回取りに行ってしまう。
                _wakePending = false;

                if (sleep is not null && ReferenceEquals(_sleep, sleep)) _sleep = null;
            }

            sleep?.Dispose();
        }
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

    private void Publish(AppState state)
    {
        Current = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        // _sleep は _stop にぶら下げた子なので、これだけで待機も同時に解ける。
        _stop.Cancel();

        bool finished = false;
        try { finished = _loop?.Wait(TimeSpan.FromSeconds(2)) ?? true; }
        catch { /* 終了時の例外は無視 */ }

        // ★ ループが終わっていないのに Dispose してはいけない。
        //   ループはまだ _stop.Token を Task.Delay に渡すので、
        //   破棄済みだと ObjectDisposedException になり、
        //   誰も見ていない Task の例外として消える（原因を追えなくなる）。
        //   終了間際にリークしても実害は無いので、生きているなら放置する。
        if (finished) _stop.Dispose();
        else Log.Warn("ポーリングの停止を確認できませんでした。");
    }
}
