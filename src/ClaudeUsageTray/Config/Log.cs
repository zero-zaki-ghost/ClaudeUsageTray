using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeUsageTray.Config;

/// <summary>
/// ローテート付きの素朴なファイルログ。
///
/// ★ すべての出力が <see cref="Redact"/> を必ず通る。呼び出し側の「マスクし忘れ」に
///   依存させないため、Write の内部で強制適用している。ここを緩めないこと。
/// </summary>
internal static partial class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly Lock Gate = new();

    /// <summary>
    /// アクセストークンは 108 文字の [A-Za-z0-9_-] 列。40 文字以上の同種の並びを
    /// まとめて伏せる。Windows パスは '\' や '.' を含むので巻き込まれない。
    /// セッション UUID は 36 文字なので閾値未満。
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])[A-Za-z0-9_\-]{40,}(?![A-Za-z0-9_\-])", RegexOptions.Compiled)]
    private static partial Regex TokenLike();

    public static string Redact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return TokenLike().Replace(s, m => $"<redacted:{m.Value.Length}>");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        // 例外の ToString() にはリクエスト情報が混ざりうるので、型名と短いメッセージだけ出す。
        var detail = ex is null ? "" : $" | {ex.GetType().Name}: {ShortMessage(ex)}";
        Write("ERROR", message + detail);
    }

    public static string ShortMessage(Exception ex)
    {
        var m = ex.Message.ReplaceLineEndings(" ");
        return m.Length > 200 ? m[..200] + "…" : m;
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {Redact(message)}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var path = Path.Combine(AppPaths.LogDir, "tray.log");

                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = Path.Combine(AppPaths.LogDir, "tray.1.log");
                    File.Delete(old);
                    File.Move(path, old);
                }

                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // ログが書けないことでアプリを落とさない。
        }
    }
}
