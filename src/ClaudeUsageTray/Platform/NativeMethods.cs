using System.Runtime.InteropServices;

namespace ClaudeUsageTray.Platform;

// =============================================================================
//  Win32 P/Invoke
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  ここに置いてよいのは「公開 API だけ」。
//
//  このアプリの表示方式を選ぶとき、タスクバーへ子ウィンドウを SetParent で
//  埋め込む方式（TrafficMonitor 方式）や Deskband など、もっと見栄えのする
//  手段はあった。すべて却下している。理由は Windows Update で壊れるから。
//    ・Deskband は Windows 11 の XAML タスクバーで完全に非対応
//    ・デスクトップツールバーは Windows 11 で機能ごと削除
//    ・SetParent 埋め込みは 24H2/25H2 で位置ずれの不具合が続いており、
//      セキュリティソフトに注入をブロックされることもある
//
//  ここで使っているのは、どれも 20 年以上安定している公開 API だけ:
//    ・SetWindowLongPtr / GetWindowLongPtr … 拡張スタイルの読み書き
//    ・RegisterHotKey / UnregisterHotKey   … グローバルホットキー
//    ・SHQueryUserNotificationState        … 全画面アプリの検出
//
//  新しい P/Invoke を足すときは「これは公開 API か」「壊れたときアプリが
//  死ぬか、機能が 1 つ減るだけで済むか」を必ず考えること。
// =============================================================================

internal static partial class NativeMethods
{
    // ---- ウィンドウの拡張スタイル ----
    //
    // オーバーレイに付ける 4 つの意味:
    //   LAYERED    : 半透明にできる（Form.Opacity が内部で要求する）
    //   TRANSPARENT: クリックを下のウィンドウへ素通りさせる
    //   TOOLWINDOW : Alt+Tab とタスクバーに出さない。常駐パネルとして正しい振る舞い
    //   NOACTIVATE : クリックしてもフォーカスを奪わない。作業の邪魔をしない

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // 32bit 版の SetWindowLong ではなく Ptr 版を使う。x64 では ExStyle も
    // ポインタ幅で扱われるため、32bit 版だと上位ビットを落とす危険がある。
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---- グローバルホットキー ----
    //
    // クリック透過を ON にするとパネルを掴めなくなるので、
    // 「掴めるようにするための入口」を OS レベルのホットキーで用意しておく。
    // これが無いと、透過を ON にした瞬間にユーザーが操作不能になる。

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;

    /// <summary>押しっぱなしでリピートしない。移動モードがばたつくのを防ぐ。</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- 全画面アプリの検出 ----
    //
    // 前景ウィンドウの矩形とモニタ矩形を比べる自前判定もできるが、
    // シェルが持っている状態をそのまま聞くほうが確実で軽い。
    // プレゼン中やゲーム中に最前面パネルが乗るのは事故なので必ず引っ込める。

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
