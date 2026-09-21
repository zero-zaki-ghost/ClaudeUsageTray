namespace ClaudeUsageTray.Core;

// =============================================================================
//  表示状態
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  「取れた / 取れない」の 2 値にしない。常駐して見続けるものなので、
//  ユーザーが次に何をすべきかが状態から分かる必要がある。
//
//    Starting     … まだ 1 回も取れていない。空白にはしない
//                   （空白だと「起動していない」と誤解される）
//    Ok           … 新しい値
//    Stale        … 値はあるが古い。捨てずに「古い」と示す
//    Offline      … 通信できない。ユーザー側にできることは無い
//    AuthRequired … Claude Code を起動すれば直る。行動可能なので分けている
//    NotAvailable … API キー / Bedrock / Vertex 構成でリミットの概念が無い。
//                   エラーではないので区別する
//
//  Offline と AuthRequired を分けているのが肝。どちらも「取れない」だが、
//  前者は待つしかなく、後者はユーザーが直せる。同じ表示にしてはいけない。
// =============================================================================
internal enum FetchStatus
{
    /// <summary>起動直後、まだ 1 回も取れていない。空白にはしないこと。</summary>
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
