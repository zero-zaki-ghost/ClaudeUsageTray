using System.Runtime.InteropServices;

namespace ClaudeUsageTray.Platform;

internal static partial class NativeMethods
{
    public const int SM_CXSMICON = 49;

    /// <summary>
    /// <see cref="System.Drawing.Bitmap.GetHicon"/> が返すハンドルの破棄に必ず使う。
    /// Icon.Dispose() では解放されない（Icon.FromHandle は所有権を取らない）。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    /// <summary>GetSystemMetrics は DPI 非対応なので、必ずこちらを使う。</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDesktopWindow();

    // ---- オーバーレイ窓 ----

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;   // クリック透過
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;   // Alt+Tab に出さない
    public const int WS_EX_NOACTIVATE = 0x08000000;   // フォーカスを奪わない

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---- グローバルホットキー ----

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- 全画面アプリの検出 ----

    public enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningDirect3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out UserNotificationState state);
}
