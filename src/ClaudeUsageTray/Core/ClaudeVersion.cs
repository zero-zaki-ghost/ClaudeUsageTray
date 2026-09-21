using System.Text.Json;
using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Core;

// =============================================================================
//  Claude Code のバージョン検出
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  User-Agent に載せるバージョンを固定値にしない。
//
//  本アプリは本体と同じ内部エンドポイントを叩くので、User-Agent も本体に
//  合わせている。ここを古いまま放置すると、本体が更新されたあと「実在しない
//  古いクライアント」を名乗り続けることになり、WAF に目を付けられる余地を作る。
//  ~/.claude/sessions/<pid>.json に本体が書いている version を読んで追従する。
//
//  読めなければ既定値にフォールバックする。ここで失敗してもアプリの機能は
//  落とさない（バージョン文字列が少し古いだけ）。
// =============================================================================
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
