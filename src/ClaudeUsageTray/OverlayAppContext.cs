using System.Windows.Forms;
using ClaudeUsageTray.Config;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Platform;
using ClaudeUsageTray.Ui;

namespace ClaudeUsageTray;

// =============================================================================
//  アプリの組み立て
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. UI はパネル 1 枚だけ。通知領域（トレイ）は持たない。
//     Windows のトレイは 16×16 のアイコン 1 枚しか置けず、5時間・週次・
//     モデル別を同時に見せられない。しかも Windows 11 は既定でオーバーフローに
//     隠すので「見るのにクリックが要る」状態になる。要求と正反対なのでやめた。
//
//  2. 生成の順序に意味がある。
//     オーバーレイは初回描画で _polling.Current を読む。ポーリングより先に
//     作ると、表示された瞬間に NullReference で落ちる。
//
//  3. UI を殺せる設定は置かない。
//     トレイが無いので、パネルを隠せるようにするとメニューに辿り着けなくなり、
//     プロセスを終了する手段まで失われる。「常時表示の ON/OFF」は意図的に
//     持たせていない（クリック透過は Ctrl+Shift+U で戻せるので可）。
//
//  4. 自動起動のチェックは OS の状態を正とする。
//     メニューを開くたびにレジストリを読み直す。ユーザーが「設定 > スタート
//     アップ」側で切った場合に、チェックだけ残って嘘をつかないようにする。
// =============================================================================

internal sealed class OverlayAppContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly PollingService _polling;
    private readonly WakeSignals _wake;
    private readonly ContextMenuStrip _menu;
    private readonly OverlayWindow _overlay;

    /// <summary>ポーリングスレッドから UI スレッドへ戻すためだけの不可視コントロール。</summary>
    private readonly Control _marshal;

    private bool _disposed;

    public OverlayAppContext(Options options)
    {
        AppPaths.EnsureAppDirectories();
        _settings = AppSettings.Load();

        _marshal = new Control();
        _marshal.CreateControl();

        _polling = new PollingService(options.Endpoint, options.IntervalSeconds);
        _polling.StateChanged += OnStateChanged;

        // OS のイベント（ネットワーク復帰・スリープ復帰・ロック解除）を
        // ポーリングの「起床」へ流す。配線をここに置いているのは、
        // Core が Platform に依存しないようにするため（PollingService の 5）。
        _wake = new WakeSignals();
        _wake.Woke += _polling.Wake;

        _menu = BuildMenu();

        _overlay = new OverlayWindow(_settings, () => _polling.Current, _menu);
        _overlay.Show();

        // ログオン直後は I/O が混むので初回取得を少し遅らせる
        _polling.Start(initialDelay: options.FromStartup
            ? TimeSpan.FromSeconds(15)
            : TimeSpan.Zero);

        Log.Info($"起動しました。endpoint={options.Endpoint} interval={options.IntervalSeconds}s "
               + $"startup={options.FromStartup}");
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("今すぐ更新", null, (_, _) => _polling.RequestRefresh());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("表示位置を動かす（Ctrl+Shift+U）", null, (_, _) => _overlay.ToggleMoveMode());

        var clickThrough = new ToolStripMenuItem("クリックを下へ透過する")
        {
            CheckOnClick = true,
            Checked = _settings.Overlay.ClickThrough,
            ToolTipText = "ONにするとパネルを掴めなくなります。移動は Ctrl+Shift+U から。",
        };
        clickThrough.CheckedChanged += (_, _) => _overlay.ClickThrough = clickThrough.Checked;
        menu.Items.Add(clickThrough);

        menu.Items.Add(new ToolStripSeparator());

        var autoStart = new ToolStripMenuItem("Windows 起動時に開始")
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled(),
        };
        // ★ ラムダで購読すると後から -= で外せない。名前付きにしておく。
        void AutoStartHandler(object? s, EventArgs e) => OnAutoStartToggled(autoStart);

        autoStart.CheckedChanged += AutoStartHandler;
        menu.Items.Add(autoStart);

        // 開くたびに実際のレジストリの状態へ合わせ直す
        // （「設定 > スタートアップ」側で無効化された場合に追従するため）。
        // 合わせ直す間はハンドラを外さないと、勝手にインストールの確認が出てしまう。
        menu.Opening += (_, _) =>
        {
            bool actual = AutoStart.IsEnabled();
            if (autoStart.Checked == actual) return;

            autoStart.CheckedChanged -= AutoStartHandler;
            autoStart.Checked = actual;
            autoStart.CheckedChanged += AutoStartHandler;
        };

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ログフォルダを開く…", null, (_, _) => OpenFolder(AppPaths.LogDir));
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        return menu;
    }

    private void OnAutoStartToggled(ToolStripMenuItem item)
    {
        if (!item.Checked)
        {
            AutoStart.Disable();
            return;
        }

        // 固定の場所に居ないと、ビルドし直したり移動した時点で自動起動が壊れる
        if (Installer.IsRunningFromInstallDir())
        {
            AutoStart.Enable(Installer.InstalledExe);
            return;
        }

        var answer = MessageBox.Show(
            "自動起動には固定の場所へ配置する必要があります。\n\n" +
            $"配置先:\n{Installer.InstallDir}\n\n" +
            "コピーして自動起動に登録し、そちらを起動し直しますか？",
            "ClaudeUsageTray", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (answer != DialogResult.OK)
        {
            item.Checked = false;
            return;
        }

        Installer.Install();
        ExitThread();   // インストール先のプロセスに引き継ぐ
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("フォルダを開けませんでした", ex);
        }
    }

    private void OnStateChanged(AppState state)
    {
        if (_disposed) return;

        try
        {
            if (_marshal.IsHandleCreated)
                _marshal.BeginInvoke(() =>
                {
                    if (!_disposed && !_overlay.IsDisposed) _overlay.Invalidate();
                });
        }
        catch (ObjectDisposedException)
        {
            // 終了処理と競合した場合。何もしない。
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            // 起床シグナルを先に切る。OS の静的イベントを掴んだままだと
            // 終了後もコールバックが飛び、このオブジェクトが回収されない。
            _wake.Woke -= _polling.Wake;
            _wake.Dispose();

            _polling.StateChanged -= OnStateChanged;
            _polling.Dispose();

            _overlay.Dispose();
            _menu.Dispose();
            _marshal.Dispose();

            Log.Info("終了しました。");
        }

        base.Dispose(disposing);
    }
}
