using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Core;

// =============================================================================
//  送る前に止める判断
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. 「送ってみて結果で判断する」をやめる。
//     実機で 2 回踏んだ 429 は、**失効が分かっているトークンを送って
//     401 を確かめに行った**のが原因だった。401 が 3 回続くとサーバーが
//     429 に切り替える。送る前に分かることは、送る前に判断する。
//
//  2. 判断を「並べられる形」にしておく。
//     止める理由はこれから増える（本体プロセスが動いていない、など）。
//     if を足していくと PollingService が再び膨らみ、条件同士の優先順位が
//     コードの行順という暗黙のものになる。Guard を順に評価する形にすれば、
//     追加は「リストに 1 つ足す」で済み、順序も明示される。
//
//  3. 止めるときは必ず「ユーザーが次に何をすればいいか」を返す。
//     止めた理由を画面に出さないと、ユーザーには「壊れて止まった」としか
//     見えない。Guard は止める判断と案内文を必ずセットで返す。
// =============================================================================

/// <summary>送ってよいか。止めるなら、そのときの状態と案内文を伴う。</summary>
internal readonly record struct SendDecision(bool ShouldSend, FetchStatus Status, string? Guidance)
{
    public static readonly SendDecision Send = new(true, FetchStatus.Ok, null);

    public static SendDecision Hold(FetchStatus status, string guidance) => new(false, status, guidance);
}

internal interface ISendGuard
{
    /// <summary>送ってよければ <see cref="SendDecision.Send"/> を返す。</summary>
    SendDecision Evaluate(Credentials credentials);
}

// =============================================================================
/// <summary>
/// 失効が分かっているトークンを送らせない。
///
/// ★ 時計ずれへの逃げ道: 端末の時計が大きくずれていると、生きているトークンを
///   失効と誤判定しうる（<c>expiresAt</c> は絶対時刻なので時計に依存する）。
///   そのとき「送らない」だけだとアプリが永久に沈黙し、しかも理由が分からない。
///   30 分に 1 回だけ送ってみて、自分の判断を疑う機会を残してある。
///
///   ただし **本体が 1 つも動いていないなら、そのプローブも要らない**。
///   トークンを更新できるのは本体だけなので、動いていなければ更新は起きない。
///   時計に依存しない確実な事実なので、こちらを優先する（懸念 8）。
/// </summary>
internal sealed class ExpiredTokenGuard(Func<bool> claudeRunning) : ISendGuard
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(30);

    internal const string Guidance = "Claude Code を起動すると自動で復帰します。";

    // 起動直後にいきなりプローブしないよう now で初期化する。
    private DateTimeOffset _lastProbe = DateTimeOffset.UtcNow;
    private bool _logged;

    public SendDecision Evaluate(Credentials credentials)
    {
        if (!credentials.IsExpired)
        {
            _logged = false;
            return SendDecision.Send;
        }

        bool running = claudeRunning();

        // 本体が動いているのに失効したままなら、こちらの時計を疑う余地がある。
        // 動いていないなら疑う必要は無い（更新できる者が居ないのだから当然失効する）。
        var now = DateTimeOffset.UtcNow;
        if (running && now - _lastProbe >= ProbeInterval)
        {
            _lastProbe = now;
            Log.Warn("本体は動いているのに失効のままなので、時計のずれを疑って 1 回だけ試します。");
            return SendDecision.Send;
        }

        if (!_logged)
        {
            _logged = true;
            Log.Warn($"トークンが {-credentials.Remaining.TotalMinutes:F0} 分前に失効しています"
                   + $"（本体は{(running ? "起動中" : "未起動")}）。更新されるまで送信を見送ります。");
        }

        return SendDecision.Hold(FetchStatus.AuthRequired, Guidance);
    }
}
