using Microsoft.Win32;

namespace ClaudeUsageTray.Platform;

internal static class ThemeDetector
{
    /// <summary>
    /// タスクバーが暗いかどうか。
    ///
    /// ★ 見るのは SystemUsesLightTheme。AppsUseLightTheme ではない（間違えやすい）。
    ///   前者がタスクバー / 通知領域、後者がアプリのウィンドウ。
    /// </summary>
    public static bool IsTaskbarDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            if (key?.GetValue("SystemUsesLightTheme") is int v)
                return v == 0;
        }
        catch
        {
            // 読めない環境ではダーク扱い（Windows 11 の既定）
        }

        return true;
    }
}
