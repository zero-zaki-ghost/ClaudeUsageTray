using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Rendering;

/// <summary>
/// 描画内容の同一性。これが等しければ HICON を作り直さない。
///
/// 2 分に 1 回のポーリングでも % が変わらなければハンドルを作らずに済み、
/// ハンドル生成回数が 1 桁減る（＝リークの機会自体が減る）。
/// </summary>
internal readonly record struct IconContentKey(
    string Text,
    int SeverityRank,
    FetchStatus Status,
    int Size,
    bool DarkTaskbar);
