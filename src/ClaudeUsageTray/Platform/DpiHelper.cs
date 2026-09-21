namespace ClaudeUsageTray.Platform;

internal static class DpiHelper
{
    /// <summary>
    /// トレイアイコンの実ピクセルサイズ。100%→16 / 125%→20 / 150%→24 / 200%→32。
    ///
    /// GetSystemMetrics(SM_CXSMICON) は DPI 非対応なので使わない。
    /// </summary>
    public static int TrayIconSize(IntPtr referenceHwnd)
    {
        uint dpi = 0;
        if (referenceHwnd != IntPtr.Zero)
            dpi = NativeMethods.GetDpiForWindow(referenceHwnd);

        if (dpi == 0)
            dpi = NativeMethods.GetDpiForWindow(NativeMethods.GetDesktopWindow());

        if (dpi == 0) dpi = 96;

        int size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi);
        if (size <= 0)
            size = (int)Math.Round(16.0 * dpi / 96.0);   // MulDiv(16, dpi, 96) 相当

        // 常識的な範囲に丸めておく（壊れた DPI 値で巨大ビットマップを作らないため）
        return Math.Clamp(size, 16, 64);
    }
}
