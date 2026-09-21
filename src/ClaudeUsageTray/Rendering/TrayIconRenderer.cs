using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Rendering;

internal static class TrayIconRenderer
{
    public static IconContentKey KeyFor(AppState state, int size, bool dark)
    {
        var headline = state.Server?.PickHeadline();
        return new IconContentKey(
            Text: TextFor(state, headline),
            SeverityRank: headline?.SeverityRank ?? 0,
            Status: state.Status,
            Size: size,
            DarkTaskbar: dark);
    }

    private static string TextFor(AppState state, LimitRow? headline) => state.Status switch
    {
        FetchStatus.Starting => "··",
        FetchStatus.AuthRequired => "!",
        FetchStatus.Offline => "--",
        FetchStatus.NotAvailable => "n/a",
        _ => headline?.Percent is { } p
            ? (p >= 100 ? "!" : ((int)Math.Round(p)).ToString())
            : "--",
    };

    /// <summary>
    /// 不透明な角丸の板 + 数字。
    ///
    /// なぜ「板」なのか（透明背景に数字だけ、ではなく）:
    ///   Windows 11 のタスクバーは壁紙のアクリル透過を受けるため、テーマ値が
    ///   ダークでも実際の背景が明るいことがある。不透明な板なら背景に関係なく読める。
    ///
    /// ★ 文字は GDI+ の DrawString + SingleBitPerPixelGridFit で描く。
    ///   ・TextRenderer(GDI) はアルファチャンネルを書かないため、32bpp ARGB の
    ///     ビットマップに描くと透明のまま＝アイコンで文字が消える。
    ///   ・SingleBitPerPixelGridFit はアンチエイリアスを切るので、16px でも
    ///     数字が潰れずに読める。
    /// </summary>
    public static Bitmap Render(AppState state, int size, bool dark)
    {
        var headline = state.Server?.PickHeadline();
        string text = TextFor(state, headline);

        bool neutral = state.Status is FetchStatus.Starting or FetchStatus.Offline
                                     or FetchStatus.NotAvailable;

        Color plate = state.Status switch
        {
            FetchStatus.AuthRequired => IconTheme.Plate(1, dark),           // 橙
            _ when neutral => IconTheme.NeutralPlate(dark),
            _ => IconTheme.Plate(headline?.SeverityRank ?? 0, dark),
        };

        // Stale は彩度を落として「今の値ではない」ことを色以外でも示す
        if (state.Status == FetchStatus.Stale) plate = Desaturate(plate);

        Color ink = IconTheme.InkOn(plate);

        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int radius = Math.Max(2, size / 5);
            using (var path = RoundedRect(new Rectangle(0, 0, size, size), radius))
            using (var brush = new SolidBrush(plate))
            {
                g.FillPath(brush, path);
            }

            // 文字はアンチエイリアス無しで
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

            var inner = new RectangleF(1, 0, size - 2, size);
            using var font = FitFont(g, text, inner, size);
            using var inkBrush = new SolidBrush(ink);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            g.DrawString(text, font, inkBrush, inner, format);
        }

        return bmp;
    }

    /// <summary>領域に収まる最大のフォントサイズを探す。文字数で見た目が破綻しないように。</summary>
    private static Font FitFont(Graphics g, string text, RectangleF area, int size)
    {
        // 16px では 2 文字が限界。3 文字("n/a")は自動的に小さくなる。
        float max = size * 0.95f;
        float min = Math.Max(5f, size * 0.35f);

        for (float em = max; em > min; em -= 0.5f)
        {
            var font = new Font("Segoe UI Semibold", em, FontStyle.Regular, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);

            if (measured.Width <= area.Width && measured.Height <= area.Height)
                return font;

            font.Dispose();
        }

        return new Font("Segoe UI Semibold", min, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
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

    private static Color Desaturate(Color c)
    {
        int gray = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
        return Color.FromArgb(c.A, gray, gray, gray);
    }
}
