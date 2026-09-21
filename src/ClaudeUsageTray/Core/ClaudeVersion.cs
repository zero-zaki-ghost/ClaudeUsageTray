using System.Text.Json;
using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Core;

/// <summary>
/// User-Agent に載せる Claude Code のバージョン。
///
/// ~/.claude/sessions/&lt;pid&gt;.json に "version" が入っているので、本体が
/// アップデートされたら自動で追従する（固定値のままだと WAF に目を付けられうる）。
/// </summary>
internal static class ClaudeVersion
{
    private const string Fallback = "2.1.278";

    private static string? _cached;
    private static DateTimeOffset _cachedAt;

    public static string Detect()
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromMinutes(30))
            return _cached;

        string version = Fallback;
        try
        {
            var newest = new DirectoryInfo(AppPaths.SessionsDir)
                .EnumerateFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is not null)
            {
                using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);

                if (doc.RootElement.TryGetProperty("version", out var v)
                    && v.GetString() is { Length: > 0 } s)
                {
                    version = s;
                }
            }
        }
        catch
        {
            // sessions ディレクトリが無い / 読めないときは既定値のまま
        }

        _cached = version;
        _cachedAt = DateTimeOffset.UtcNow;
        return version;
    }
}
