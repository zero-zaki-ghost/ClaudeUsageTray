namespace ClaudeUsageTray.Core;

// =============================================================================
//  リミット 1 行
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  「サーバーが言うことをそのまま信じ、こちらで解釈を足さない」。
//
//  レスポンスには five_hour / seven_day のほか nimbus_quill / cinder_cove /
//  tangelo / harbor_lantern といったコードネームの窓が 20 個近く並んでおり、
//  その大半は null。名前から意味を推測して分岐を書くと、名前が変わった日に
//  静かに壊れる。サーバー側の内部スキーマにも
//    "Classify a row on this, never on a label."
//  と明記されている。だから分類は limits[] の kind だけで行う。
//
//  この方針から決まること:
//
//  ・kind と severity は string のまま持つ。enum にしない。
//    未知の値が enum の 0 番（= session）に落ちると、別のリミットとして
//    誤って合算される。型の便利さより誤りの起きなさを取る。
//
//  ・未知の kind を捨てない。「その他」として素通しで表示する。
//    新しいリミットが追加されたとき、黙って消えるより見えたほうが良い。
//
//  ・severity の順位付けでも、未知の値は 0（normal）ではなく 1 に置く。
//    新しい警告レベルが増えたときに見落とさないため。安全側に倒す。
//
//  ・しきい値を自前で持たない。色分けも警告もサーバーの severity に従う。
//    自前で 80% / 90% を決めると、公式 UI と表示が食い違って混乱する。
// =============================================================================

/// <summary>
/// limits[] の 1 行を正規化したもの。
///
/// ★ Kind / Severity は string のまま保持する。enum にすると未知の値が 0 番
///   （= session など）に落ちて別リミットと誤合算する。
/// </summary>
internal sealed record LimitRow(
    string Kind,
    string? Group,
    double? Percent,
    string Severity,
    DateTimeOffset? ResetsAt,
    bool IsActive,
    string? ScopeName)
{
    public bool IsSession => Kind == "session";
    public bool IsWeeklyAll => Kind == "weekly_all";
    public bool IsWeeklyScoped => Kind == "weekly_scoped";

    public string Label => Kind switch
    {
        "session" => "セッション (5h)",
        "weekly_all" => "週次 (全体)",
        "weekly_scoped" => $"週次 {ScopeName ?? "?"}",
        _ => Kind,   // 未知 kind も捨てずにそのまま出す
    };

    /// <summary>
    /// 深刻度の順序。未知の値は 1（warning 相当）に置く。
    /// 0 に落とすと新しい警告レベルを見落とすため。
    /// </summary>
    public int SeverityRank => Severity switch
    {
        "normal" => 0,
        "warning" => 1,
        "critical" => 2,
        _ => 1,
    };

    public static LimitRow? From(LimitRowDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Kind)) return null;

        DateTimeOffset? resets = null;
        if (!string.IsNullOrWhiteSpace(dto.ResetsAt)
            && DateTimeOffset.TryParse(dto.ResetsAt, out var parsed))
        {
            resets = parsed;
        }

        string? scopeName = dto.Scope?.Model?.DisplayName ?? dto.Scope?.Surface?.DisplayName;

        return new LimitRow(
            Kind: dto.Kind!,
            Group: dto.Group,
            Percent: dto.Percent,
            Severity: dto.Severity ?? "normal",
            ResetsAt: resets,
            IsActive: dto.IsActive ?? false,
            ScopeName: scopeName);
    }
}
