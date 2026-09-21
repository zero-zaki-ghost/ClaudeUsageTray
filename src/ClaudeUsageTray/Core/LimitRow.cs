namespace ClaudeUsageTray.Core;

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
