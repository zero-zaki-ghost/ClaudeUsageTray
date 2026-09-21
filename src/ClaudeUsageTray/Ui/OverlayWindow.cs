using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using ClaudeUsageTray.Config;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Platform;
using ClaudeUsageTray.Rendering;

namespace ClaudeUsageTray.Ui;

/// <summary>
/// 画面に常時出しておく最前面パネル。
///
/// トレイアイコンは 16×16 に 2〜3 文字しか入らないため、5時間 / 週次 / モデル別を
/// 同時に見せられない。こちらは任意幅のテキストをそのまま出せる。
/// 使う API はすべて公開 API なので Windows Update で壊れにくい。
///
/// 操作:
///   Ctrl+Shift+U … 移動モードの切り替え（クリック透過を解除してドラッグ可能にする）
///   移動モード中の右クリック … メニュー
/// </summary>
internal sealed class OverlayWindow : Form
{
    private const int HotkeyIdMove = 0xC1A0;

    private readonly AppSettings _settings;
    private readonly Func<AppState> _getState;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _tick;

    private bool _moveMode;
    private bool _hiddenByFullscreen;
    private Point _dragOrigin;
    private bool _dragging;
    private bool _hovered;

    /// <summary>
    /// ★ CreateParams に含めるのが肝。SetWindowLongPtr で後付けするだけだと、
    ///   Opacity を変えたときに WinForms が CreateParams から ExStyle を作り直して
    ///   このフラグを消してしまう（透過が勝手に外れる／戻らなくなる）。
    /// </summary>
    private bool _clickThrough;

    public OverlayWindow(AppSettings settings, Func<AppState> getState, ContextMenuStrip menu)
    {
        _settings = settings;
        _getState = getState;
        _menu = menu;
        _clickThrough = settings.Overlay.ClickThrough;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AllowTransparency = true;           // WS_EX_LAYERED が付く（Opacity のため）
        Opacity = settings.Overlay.Opacity;
        BackColor = Color.FromArgb(18, 18, 20);
        DoubleBuffered = true;
        Size = new Size(320, 30);

        // 1 秒ごとにカウントダウンを更新する
        _tick = new System.Windows.Forms.Timer { Interval = 1000 };
        _tick.Tick += (_, _) => { UpdateFullscreenVisibility(); Invalidate(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Alt+Tab に出さず、フォーカスも奪わない
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

            // 移動モード中は必ず掴めるようにする
            if (_clickThrough && !_moveMode) cp.ExStyle |= NativeMethods.WS_EX_TRANSPARENT;

            return cp;
        }
    }

    /// <summary>クリックを下のウィンドウへ素通りさせるかどうかを実ウィンドウへ反映する。</summary>
    private void ApplyClickThrough()
    {
        if (!IsHandleCreated) return;

        bool on = _clickThrough && !_moveMode;
        var ex = (long)NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE);

        if (on) ex |= NativeMethods.WS_EX_TRANSPARENT;
        else ex &= ~NativeMethods.WS_EX_TRANSPARENT;

        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
    }

    /// <remarks>
    /// デザイナ用のコンポーネントではないのでシリアライズさせない（WFO1000 対策）。
    /// </remarks>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value) return;

            _clickThrough = value;
            _settings.Overlay.ClickThrough = value;
            _settings.Save();

            ApplyClickThrough();
            Invalidate();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        ApplyClickThrough();
        RestorePosition();

        if (!NativeMethods.RegisterHotKey(Handle, HotkeyIdMove,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
                (uint)Keys.U))
        {
            Log.Warn("Ctrl+Shift+U を登録できませんでした（他のアプリが使用中の可能性）。");
        }

        _tick.Start();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyIdMove)
        {
            ToggleMoveMode();
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// クリック透過を ON にしていると掴めないので、一時的に解除して動かせるようにする。
    /// 透過が OFF のときは常に掴めるので、この切り替えは主に見た目の合図。
    /// </summary>
    public void ToggleMoveMode()
    {
        _moveMode = !_moveMode;

        // ★ Opacity を先に変える。後にすると WinForms が ExStyle を作り直して
        //   直前に立てた WS_EX_TRANSPARENT を消してしまう。
        Opacity = _moveMode ? 1.0 : _settings.Overlay.Opacity;
        ApplyClickThrough();

        if (!_moveMode) SavePosition();

        Invalidate();
    }

    // ---- ドラッグ移動 ----

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragOrigin = e.Location;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
            Location = new Point(Location.X + e.X - _dragOrigin.X, Location.Y + e.Y - _dragOrigin.Y);

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            SavePosition();
        }

        if (e.Button == MouseButtons.Right)
            _menu.Show(this, e.Location);

        base.OnMouseUp(e);
    }

    // ---- 位置の保存と復元 ----

    private Screen TargetScreen()
    {
        var name = _settings.Overlay.MonitorDeviceName;
        return Screen.AllScreens.FirstOrDefault(s => s.DeviceName == name) ?? Screen.PrimaryScreen!;
    }

    private void RestorePosition()
    {
        var wa = TargetScreen().WorkingArea;
        var o = _settings.Overlay;

        int x = wa.Left + (int)Math.Round((wa.Width - Width) * Math.Clamp(o.RelativeX, 0, 1));
        int y = wa.Top + (int)Math.Round((wa.Height - Height) * Math.Clamp(o.RelativeY, 0, 1));

        // モニタ構成が変わっていても画面内に収める
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));

        Location = new Point(x, y);
    }

    private void SavePosition()
    {
        var screen = Screen.FromControl(this);
        var wa = screen.WorkingArea;
        var o = _settings.Overlay;

        o.MonitorDeviceName = screen.DeviceName;
        o.RelativeX = wa.Width - Width <= 0 ? 0 : (double)(Location.X - wa.Left) / (wa.Width - Width);
        o.RelativeY = wa.Height - Height <= 0 ? 0 : (double)(Location.Y - wa.Top) / (wa.Height - Height);

        _settings.Save();
    }

    // ---- 全画面アプリ中は引っ込める ----

    private void UpdateFullscreenVisibility()
    {
        if (!_settings.Overlay.HideOnFullscreen) return;

        bool hide = false;
        if (NativeMethods.SHQueryUserNotificationState(out var state) == 0)
        {
            hide = state is NativeMethods.UserNotificationState.RunningDirect3dFullScreen
                          or NativeMethods.UserNotificationState.PresentationMode
                          or NativeMethods.UserNotificationState.Busy;
        }

        if (hide == _hiddenByFullscreen) return;

        _hiddenByFullscreen = hide;
        Visible = !hide;
    }

    // ---- 描画 ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        var state = _getState();
        var segments = BuildSegments(state);

        using var font = new Font("Segoe UI Semibold", _settings.Overlay.FontSizePx,
            FontStyle.Regular, GraphicsUnit.Pixel);
        using var sepBrush = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
        // ★ MeasureTrailingSpaces が無いと GenericTypographic は末尾の空白を幅に数えず、
        //   次のセグメントが詰まって "5h32%" のようにくっつく。
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        };

        const int padX = 10;
        float x = padX;
        float totalH = 0;

        foreach (var (text, color) in segments)
        {
            var size = g.MeasureString(text, font, PointF.Empty, format);
            totalH = Math.Max(totalH, size.Height);

            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, new PointF(x, 0), format);
            x += size.Width;
        }

        // 実際の内容に合わせて幅・高さを詰める
        int wantW = (int)Math.Ceiling(x) + padX;
        int wantH = (int)Math.Ceiling(totalH) + 8;
        if (Math.Abs(Width - wantW) > 1 || Math.Abs(Height - wantH) > 1)
        {
            Size = new Size(Math.Max(80, wantW), Math.Max(20, wantH));
            Region = new Region(RoundedPath(new Rectangle(0, 0, Width, Height), 6));
            Invalidate();
            return;
        }

        // 文字の縦位置を中央へ寄せ直す
        float top = (Height - totalH) / 2f;
        if (top > 1)
        {
            g.Clear(BackColor);
            x = padX;
            foreach (var (text, color) in segments)
            {
                using var brush = new SolidBrush(color);
                g.DrawString(text, font, brush, new PointF(x, top), format);
                x += g.MeasureString(text, font, PointF.Empty, format).Width;
            }
        }

        // 掴めることの合図。移動モードは赤枠、ホバー中は控えめな白枠。
        if (_moveMode)
        {
            using var pen = new Pen(Color.FromArgb(230, 0xE5, 0x48, 0x4D), 2);
            g.DrawRectangle(pen, 1, 1, Width - 2, Height - 2);
        }
        else if (_hovered && !_clickThrough)
        {
            using var pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            DrawGrip(g);
        }
    }

    /// <summary>左端に点 6 個のグリップを描く。「ここを掴んで動かせる」の合図。</summary>
    private void DrawGrip(Graphics g)
    {
        using var brush = new SolidBrush(Color.FromArgb(120, 255, 255, 255));
        int cx = 4, cy = Height / 2;

        for (int col = 0; col < 2; col++)
            for (int row = -1; row <= 1; row++)
                g.FillRectangle(brush, cx + col * 3, cy + row * 4 - 1, 2, 2);
    }

    /// <summary>表示テキストを「文字列 + 色」の並びとして組み立てる。</summary>
    private static List<(string Text, Color Color)> BuildSegments(AppState state)
    {
        var list = new List<(string, Color)>();
        var dim = Color.FromArgb(150, 255, 255, 255);
        var normal = Color.FromArgb(235, 255, 255, 255);

        if (state.Status == FetchStatus.Starting || state.Server is null)
        {
            list.Add(("Claude 使用量  ", normal));
            list.Add((state.StatusDetail ?? "取得中…", dim));
            return list;
        }

        var server = state.Server;
        bool first = true;

        void AddLimit(string label, LimitRow? row)
        {
            if (row is null) return;

            if (!first) list.Add(("  │  ", dim));
            first = false;

            list.Add(($"{label} ", dim));
            list.Add(($"{Pct(row.Percent)}", IconTheme.Plate(row.SeverityRank, dark: true)));

            if (row.ResetsAt is { } r)
                list.Add(($" {Remaining(r)}", dim));
        }

        AddLimit("5h", server.Session);
        AddLimit("週", server.WeeklyAll);

        foreach (var scoped in server.WeeklyScoped)
            AddLimit(scoped.ScopeName ?? "model", scoped);

        if (state.Status == FetchStatus.AuthRequired)
            list.Add(("  │  Claude Code を起動してください", IconTheme.Plate(1, dark: true)));
        else if (state.Status == FetchStatus.Offline)
            list.Add(("  │  接続できません", IconTheme.Plate(1, dark: true)));
        else if (state.Age is { } age && age > TimeSpan.FromMinutes(5))
            list.Add(($"  │  {(int)age.TotalMinutes}分前", dim));

        return list;
    }

    private static string Pct(double? p) => p is null ? "--" : $"{Math.Round(p.Value)}%";

    /// <summary>リセットまでの残り。1 時間未満は分秒、1 日未満は時分、それ以上は日時。</summary>
    private static string Remaining(DateTimeOffset resetsAt)
    {
        var left = resetsAt - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return "まもなく";

        if (left < TimeSpan.FromHours(1)) return $"{left.Minutes}:{left.Seconds:00}";
        if (left < TimeSpan.FromDays(1)) return $"{(int)left.TotalHours}:{left.Minutes:00}";
        return $"{(int)left.TotalDays}d{left.Hours}h";
    }

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Stop();
            _tick.Dispose();
            if (IsHandleCreated) NativeMethods.UnregisterHotKey(Handle, HotkeyIdMove);
        }

        base.Dispose(disposing);
    }
}
