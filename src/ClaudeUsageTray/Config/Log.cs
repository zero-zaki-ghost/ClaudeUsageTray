using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeUsageTray.Config;

// =============================================================================
//  ログ
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. マスクを「呼び出し側の責任」にしない。
//     アクセストークンを扱うアプリなので、ログに一度でも漏れれば事故になる。
//     「気をつけて書く」運用は必ずいつか破られるので、Write の内部で
//     Redact() を強制的に通している。呼び出し側が何を渡しても伏せられる。
//     ここを「性能のために」緩めないこと。
//
//  2. 例外はそのまま出さない。
//     ex.ToString() にはリクエスト情報が混ざりうる。型名と短いメッセージだけを
//     出し、スタックトレースは残さない。デバッグしづらさより漏洩しないことを取る。
//
//  3. ログが書けなくてもアプリを止めない。
//     常駐アプリなので、ディスクフルや権限の問題で落ちるほうが害が大きい。
//     Write は例外を握り潰す。
//
//  4. ログライブラリを入れない。
//     常駐アプリの依存は少ないほど良く、この規模なら 70 行で足りる。
// =============================================================================

/// <summary>ローテート付きの素朴なファイルログ。</summary>
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
