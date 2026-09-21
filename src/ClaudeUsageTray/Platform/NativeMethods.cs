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
}
