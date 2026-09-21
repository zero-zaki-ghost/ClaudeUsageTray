namespace ClaudeUsageTray.Config;

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
