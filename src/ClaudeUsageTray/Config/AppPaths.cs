namespace ClaudeUsageTray.Config;

// =============================================================================
//  パスの集約
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  参照するパスをここ 1 箇所に集める。理由は 2 つ。
//
//  1. 読み取り先が「他人のアプリのデータ」だから。
//     ~/.claude 配下は Claude Code 本体のものであり、こちらが勝手に構造を
//     仮定して各所に散らすと、本体の変更に追従できなくなる。窓口を 1 つに
//     しておけば、変わったときの修正箇所が 1 つで済む。
//
//  2. CLAUDE_CONFIG_DIR を尊重するため。
//     本体が設定ディレクトリの移動に対応している以上、こちらも合わせる。
//     ハードコードすると、移動している環境で黙って動かなくなる。
//
//  自分のデータ（設定・キャッシュ・ログ）は %LOCALAPPDATA% 配下に置き、
//  ~/.claude には絶対に書かない。「読むだけ」が本アプリの原則。
// =============================================================================
/// <summary>
/// 参照するパスを 1 箇所に集約する。テストで差し替えられるよう静的プロパティは
/// すべて遅延評価にしてある。
/// </summary>
internal static class AppPaths
{
    /// <summary>Claude Code の設定ディレクトリ。既定は %USERPROFILE%\.claude。</summary>
    public static string ClaudeHome =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string CredentialsFile => Path.Combine(ClaudeHome, ".credentials.json");

    /// <summary>プロセスごとに 1 ファイル。sessionId / cwd / status / version が入っている。</summary>
    public static string SessionsDir => Path.Combine(ClaudeHome, "sessions");

    public static string ProjectsDir => Path.Combine(ClaudeHome, "projects");

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageTray");

    public static string CacheDir => Path.Combine(AppDataDir, "cache");

    public static string LogDir => Path.Combine(AppDataDir, "logs");

    public static void EnsureAppDirectories()
    {
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(LogDir);
    }
}
