namespace ClaudeUsageTray.Core;

// =============================================================================
//  ポーリングの間隔だけを決める
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. ここは「いつ次を投げるか」しか知らない。
//     HTTP も認証情報もログも UI も知らない。外から見えるのは
//     「成功した / 失敗した / 送らなかった」という 3 つの報告と、
//     「次はいつ投げるか」という 1 つの答えだけ。
//
//     分離した理由は、間隔の判断が**最も間違えやすく、最も観測しにくい**から。
//     実機で 429 を出すまで誰も気づけなかったのは、この判断が取得処理・
//     例外処理・状態発行と同じメソッドに混ざっていて、単体で動かせなかったため。
//     ここだけなら時計も通信も要らずにテストできる。
//
//  2. 待ち時間は **短くする方向に働かない**。
//     どの経路を通っても下限（既定 60 秒）を下回らない。
//     Claude Code 本体が 60 秒でスロットルしている以上、それより速くは叩かない。
//
//  3. サーバーの指示はこちらの都合より強い。
//     Retry-After が来た周期は「割り込み不可」として返す。ネットワーク復帰などの
//     ローカルなイベントで早めてはいけない。**相手が待てと言っているのだから待つ。**
//
//  4. 1 回の成功で元に戻さない。
//     サーバーがまだ締めている最中に通常ペースへ戻ると、また締められる。
//     連続 3 回の成功を確認してから戻す。
// =============================================================================

/// <summary>次にいつ投げるか。<paramref name="Interruptible"/> が false なら外部イベントで早めない。</summary>
internal readonly record struct NextPoll(TimeSpan Delay, bool Interruptible);

internal sealed class PollSchedule
{
    /// <summary>バックオフを解除するのに要る連続成功の回数。</summary>
    private const int SuccessesToRecover = 3;

    /// <summary>
    /// 5xx / タイムアウト / 429（Retry-After 無し）の待ち時間。
    /// 通常間隔より短い段は意味が無いので、実際には通常間隔との大きいほうを採る。
    ///
    /// 上限を 30 分まで伸ばせるのは、ネットワーク復帰などの起床シグナルで
    /// すぐ拾い直せるから（WakeSignals）。シグナルが無い設計なら、
    /// 復帰の検知が丸ごと遅れるのでここまで伸ばせない。
    /// </summary>
    private static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(240),
        TimeSpan.FromSeconds(480),
        TimeSpan.FromSeconds(900),
        TimeSpan.FromSeconds(1800),
    ];

    /// <summary>本体が動いていないときに通常間隔へ掛ける倍率（120 秒 → 600 秒）。</summary>
    private const double IdleFactor = 5.0;

    private readonly TimeSpan _normal;
    private readonly TimeSpan _min;
    private readonly Random _jitter = new();

    /// <summary>
    /// Claude Code 本体が 1 つも動いていない状態か。
    ///
    /// ★ true でも取得を止めてはいけない。週次リミットはアカウント単位なので、
    ///   別の端末や claude.ai の利用でも数字は動く。**間隔を伸ばすだけ。**
    /// </summary>
    public bool Idle { get; set; }

    private TimeSpan Normal => Idle
        ? TimeSpan.FromSeconds(_normal.TotalSeconds * IdleFactor)
        : _normal;

    private int _failures;
    private int _successStreak;
    private TimeSpan? _retryAfter;

    public PollSchedule(TimeSpan normalInterval, TimeSpan minInterval)
    {
        _min = minInterval;
        _normal = normalInterval < minInterval ? minInterval : normalInterval;
    }

    public int ConsecutiveFailures => _failures;

    /// <summary>連続 3 回の成功でバックオフを解除する。1 回では戻さない。</summary>
    /// <returns>この成功で通常ペースに戻ったなら true。</returns>
    public bool RecordSuccess()
    {
        _retryAfter = null;

        if (_failures == 0) return false;

        if (++_successStreak < SuccessesToRecover) return false;

        _failures = 0;
        _successStreak = 0;
        return true;
    }

    public void RecordFailure(TimeSpan? retryAfter = null)
    {
        _successStreak = 0;
        if (_failures < int.MaxValue) _failures++;
        if (retryAfter is { } ra && ra > TimeSpan.Zero) _retryAfter = ra;
    }

    /// <summary>
    /// 1 通も送らなかった周期の待ち時間。
    ///
    /// バックオフを持ち越さない。サーバーに何もしていないのだから遠慮は不要で、
    /// 持ち越すと**送っていないのに復帰の検知だけが遅くなる**。
    /// 失敗回数と Retry-After は、次に実際に送るときのために残しておく。
    /// </summary>
    public NextPoll AfterSkip() => new(Jitter(Normal, 0.1), Interruptible: true);

    /// <summary>実際に送った周期の待ち時間。</summary>
    public NextPoll AfterSend()
    {
        // ① サーバーが明示的に待てと言ってきたら、それが最優先。
        if (_retryAfter is { } asked)
        {
            _retryAfter = null;
            var wait = asked < _min ? _min : asked;

            // ジッタは増やす側にだけ振る。言われた時間より早く叩かないため。
            wait = TimeSpan.FromSeconds(wait.TotalSeconds * (1.0 + _jitter.NextDouble() * 0.2));

            // ★ ローカルなイベントで割り込ませない。
            return new NextPoll(wait, Interruptible: false);
        }

        // ② 連続失敗中は段階的に延ばす。通常間隔より短い段は使わない。
        if (_failures > 0)
        {
            var step = Ladder[Math.Min(_failures - 1, Ladder.Length - 1)];
            var floor = Normal;
            return new NextPoll(Jitter(step > floor ? step : floor, 0.2), Interruptible: true);
        }

        // ③ 通常。複数インスタンスや復帰時に時刻が揃うのを避ける。
        return new NextPoll(Jitter(Normal, 0.1), Interruptible: true);
    }

    private TimeSpan Jitter(TimeSpan span, double ratio)
    {
        double factor = 1.0 - ratio + _jitter.NextDouble() * ratio * 2.0;
        var result = TimeSpan.FromSeconds(span.TotalSeconds * factor);
        return result < _min ? _min : result;
    }
}
