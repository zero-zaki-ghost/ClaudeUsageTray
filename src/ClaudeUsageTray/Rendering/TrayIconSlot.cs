using System.Drawing;
using System.Windows.Forms;
using ClaudeUsageTray.Platform;

namespace ClaudeUsageTray.Rendering;

/// <summary>
/// トレイアイコンの HICON 寿命管理。
///
/// ★★ 不変条件: Bitmap.GetHicon() の呼び出しは、アプリ全体でこのクラスの 1 箇所だけ。
///    他のどこにも増やさないこと（コードレビュー観点）。
///
/// なぜ集約するか:
///   GetHicon() が返すハンドルは「呼び出し側の所有」で、Icon.FromHandle() は
///   所有権を取らない。つまり Icon.Dispose() しても DestroyIcon されない。
///   定期更新するアプリはここで必ず GDI ハンドルをリークする
///   （Microsoft PowerToys も実際に踏んで修正 PR #50638 が出ている）。
/// </summary>
internal sealed class TrayIconSlot(NotifyIcon notifyIcon) : IDisposable
{
    private Icon? _currentIcon;
    private IntPtr _currentHIcon = IntPtr.Zero;
    private IconContentKey _currentKey;
    private bool _hasKey;

    /// <summary>描画内容が前回と同じなら何もしない（HICON を作らない）。</summary>
    public void Update(IconContentKey key, Func<Bitmap> render)
    {
        if (_hasKey && key == _currentKey) return;

        using var bmp = render();

        IntPtr hIcon = bmp.GetHicon();
        Icon? newIcon = null;
        try
        {
            newIcon = Icon.FromHandle(hIcon);

            var oldIcon = _currentIcon;
            var oldHIcon = _currentHIcon;

            // ① 先に新しいものをセットする
            notifyIcon.Icon = newIcon;

            _currentIcon = newIcon;
            _currentHIcon = hIcon;
            _currentKey = key;
            _hasKey = true;

            // 所有権を移したので finally では解放しない
            newIcon = null;
            hIcon = IntPtr.Zero;

            // ② その後で古いものを解放する。
            //    逆順にするとシェルが描画中のアイコンを消してちらつく。
            oldIcon?.Dispose();
            if (oldHIcon != IntPtr.Zero) NativeMethods.DestroyIcon(oldHIcon);
        }
        finally
        {
            // 例外で ① に到達しなかった場合だけここで後始末する
            newIcon?.Dispose();
            if (hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(hIcon);
        }
    }

    /// <summary>DPI やテーマが変わったときに強制再描画させる。</summary>
    public void Invalidate() => _hasKey = false;

    public void Dispose()
    {
        notifyIcon.Icon = null;
        _currentIcon?.Dispose();
        _currentIcon = null;

        if (_currentHIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_currentHIcon);
            _currentHIcon = IntPtr.Zero;
        }
    }
}
