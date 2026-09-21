namespace ClaudeUsageTray.Core;

internal enum FetchStatus
{
    /// <summary>起動直後、まだ 1 回も取れていない。空白アイコンにはしないこと。</summary>
    Starting,

    Ok,

    /// <summary>取れてはいるが古い。前回値をグレースケールで出す。</summary>
    Stale,

    Offline,

    /// <summary>401 が解決しない。「Claude Code を起動してください」と案内する。</summary>
    AuthRequired,

    /// <summary>limits[] が空 / 403。プランリミットの概念が無い構成。</summary>
    NotAvailable,
}

internal sealed record AppState(
    ServerUsage? Server,
    FetchStatus Status,
    string? StatusDetail)
{
    public static readonly AppState Initial = new(null, FetchStatus.Starting, null);

    public DateTimeOffset? FetchedAt => Server?.FetchedAt;

    public TimeSpan? Age => Server is null ? null : DateTimeOffset.UtcNow - Server.FetchedAt;
}
