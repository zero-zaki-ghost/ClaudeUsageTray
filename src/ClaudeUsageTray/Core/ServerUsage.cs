namespace ClaudeUsageTray.Core;

// =============================================================================
//  サーバー由来の使用量スナップショット
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  limits[] を「意味のある単位」に切り分けて UI へ渡す層。
//  ここでも kind による分類だけを使い、配列の順序や位置には依存しない。
//
//  PickHeadline() は、表示領域が 1 つしか無い場合（かつてのトレイアイコン）に
//  どれを見せるかを決めるためのもの。現在のパネルは全部並べられるので必須では
//  ないが、「今いちばん逼迫しているのはどれか」という問いは通知や将来の縮退表示
//  でも使うので残してある。
//
//  選び方の根拠:
//    1. 上限に達しているものが最優先。手が止まっているので他はどうでもよい
//    2. 次に severity が高いもの。しきい値はサーバー任せ
//    3. 平常時は 5 時間枠。分単位で動き「今この瞬間」に効くのはこれだけで、
//       週次は数日スケールなので常時見ていても動かない
// =============================================================================
internal sealed record ServerUsage(DateTimeOffset FetchedAt, IReadOnlyList<LimitRow> Limits)
{
    public LimitRow? Session => Limits.FirstOrDefault(l => l.IsSession);

    public LimitRow? WeeklyAll => Limits.FirstOrDefault(l => l.IsWeeklyAll);

    /// <summary>Fable / Opus / Sonnet … モデルが増える前提でリスト駆動。</summary>
    public IReadOnlyList<LimitRow> WeeklyScoped =>
        Limits.Where(l => l.IsWeeklyScoped).OrderByDescending(l => l.Percent ?? 0).ToList();

    /// <summary>既知の kind に当てはまらなかった行。捨てずに「その他」として扱う。</summary>
    public IReadOnlyList<LimitRow> Other =>
        Limits.Where(l => !l.IsSession && !l.IsWeeklyAll && !l.IsWeeklyScoped).ToList();

    /// <summary>
    /// false なら「プランリミットの概念が無い」構成（API キー / Bedrock / Vertex 等）。
    /// UI は N/A にフォールバックする。
    /// </summary>
    public bool RateLimitsAvailable => Limits.Count > 0;

    /// <summary>
    /// アイコンに出す代表値を選ぶ。
    ///   1. 100% 以上があればそれ
    ///   2. severity が最も高いもの（同率なら percent 最大）
    ///   3. 全部 normal なら session（5 時間）
    ///
    /// 5 時間を既定にする根拠: 分単位で動き「今この瞬間に手が止まる」唯一の指標。
    /// 週次は数日スケールなので常時監視の価値が低い。
    /// </summary>
    public LimitRow? PickHeadline()
    {
        if (Limits.Count == 0) return null;

        var capped = Limits.Where(l => l.Percent >= 100).MaxBy(l => l.Percent);
        if (capped is not null) return capped;

        var worst = Limits
            .OrderByDescending(l => l.SeverityRank)
            .ThenByDescending(l => l.Percent ?? 0)
            .First();

        return worst.SeverityRank > 0 ? worst : (Session ?? worst);
    }
}
