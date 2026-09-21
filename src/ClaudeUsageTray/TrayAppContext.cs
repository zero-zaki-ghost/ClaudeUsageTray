using System.Text;
using System.Windows.Forms;
using ClaudeUsageTray.Config;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Platform;
using ClaudeUsageTray.Rendering;

namespace ClaudeUsageTray;

internal sealed class TrayAppContext : ApplicationContext
{
    /// <summary>NotifyIcon.Text の上限。.NET Framework 時代の 63 / .NET Core の 127 と
    /// 情報が割れているので、短い方でクランプして ArgumentException を踏まない。</summary>
    private const int TooltipMaxChars = 63;

    private readonly Options _options;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayIconSlot _slot;
    private readonly PollingService _polling;

    /// <summary>ポーリングスレッドから UI スレッドへ戻すためだけの不可視コントロール。</summary>
    private readonly Control _marshal;

    private bool _disposed;

    public TrayAppContext(Options options)
    {
        _options = options;
        AppPaths.EnsureAppDirectories();

        _marshal = new Control();
        _marshal.CreateControl();

        _notifyIcon = new NotifyIcon
        {
            Text = "Claude 使用量",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _slot = new TrayIconSlot(_notifyIcon);
        Redraw(AppState.Initial);

        _polling = new PollingService(options.Endpoint, options.IntervalSeconds);
        _polling.StateChanged += OnStateChanged;
        _polling.Start();

        Log.Info($"起動しました。endpoint={options.Endpoint} interval={options.IntervalSeconds}s");

        if (options.IconStress) StartIconStress();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("今すぐ更新", null, (_, _) => _polling.RequestRefresh());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ログフォルダを開く…", null, (_, _) => OpenFolder(AppPaths.LogDir));
        menu.Items.Add("キャッシュフォルダを開く…", null, (_, _) => OpenFolder(AppPaths.CacheDir));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        return menu;
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
                _marshal.BeginInvoke(() => Redraw(state));
        }
        catch (ObjectDisposedException)
        {
            // 終了処理と競合した場合。何もしない。
        }
    }

    private void Redraw(AppState state)
    {
        if (_disposed) return;

        bool dark = ThemeDetector.IsTaskbarDark();
        int size = DpiHelper.TrayIconSize(_marshal.Handle);

        var key = TrayIconRenderer.KeyFor(state, size, dark);
        _slot.Update(key, () => TrayIconRenderer.Render(state, size, dark));

        _notifyIcon.Text = Clamp(BuildTooltip(state));
    }

    private static string Clamp(string s) =>
        s.Length <= TooltipMaxChars ? s : s[..(TooltipMaxChars - 1)] + "…";

    private static string BuildTooltip(AppState state)
    {
        if (state.Status == FetchStatus.Starting) return "Claude 使用量 — 取得中…";

        if (state.Server is null)
            return $"Claude 使用量 — {state.StatusDetail ?? "未取得"}";

        var sb = new StringBuilder();
        var session = state.Server.Session;
        var weekly = state.Server.WeeklyAll;

        sb.Append("5h ").Append(Pct(session?.Percent));
        if (weekly is not null) sb.Append(" · 週 ").Append(Pct(weekly.Percent));

        // 逼迫している方のリセット時刻を出す
        var next = new[] { session, weekly }
            .Where(l => l?.ResetsAt is not null)
            .OrderBy(l => l!.ResetsAt)
            .FirstOrDefault();

        if (next?.ResetsAt is { } r)
            sb.Append("\nリセット ").Append(r.ToLocalTime().ToString("M/d HH:mm"));

        if (state.Status == FetchStatus.AuthRequired)
            sb.Append("\nClaude Code を起動してください");
        else if (state.Status == FetchStatus.Offline)
            sb.Append("\n接続できません");

        return sb.ToString();
    }

    private static string Pct(double? p) => p is null ? "--" : $"{Math.Round(p.Value)}%";

    /// <summary>
    /// GDI ハンドルリークの加速テスト。タスクマネージャーの「GDI オブジェクト」列を
    /// 見ながら走らせ、10,000 回後に起動時 +10 以内なら合格。
    /// </summary>
    private void StartIconStress()
    {
        Log.Warn("--icon-stress: アイコンを 50ms 間隔で 10,000 回再生成します。");

        var timer = new System.Windows.Forms.Timer { Interval = 50 };
        int n = 0;

        timer.Tick += (_, _) =>
        {
            if (++n > 10_000)
            {
                timer.Stop();
                timer.Dispose();
                Log.Warn("--icon-stress: 完了。");
                return;
            }

            // 毎回キーを変えて強制的に HICON を作り直させる
            var fake = new AppState(
                new ServerUsage(DateTimeOffset.UtcNow,
                    [new LimitRow("session", "session", n % 100, "normal", null, true, null)]),
                FetchStatus.Ok, null);

            Redraw(fake);
        };

        timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            _polling.StateChanged -= OnStateChanged;
            _polling.Dispose();

            // ★ これを忘れるとトレイにゴーストアイコンが残る
            _notifyIcon.Visible = false;

            _slot.Dispose();
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _marshal.Dispose();

            Log.Info("終了しました。");
        }

        base.Dispose(disposing);
    }
}
