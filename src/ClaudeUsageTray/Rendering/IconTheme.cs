using System.Drawing;

namespace ClaudeUsageTray.Rendering;

internal static class IconTheme
{
    /// <summary>
    /// severity ごとの板の色。
    ///
    /// ★ しきい値は自前で決めず、サーバーの severity に従う。自前で 80/90% を
    ///   決めると公式 UI と表示が食い違う。
    /// ★ 緑は使わない。青→琥珀→赤は第 1・第 2 色覚でも明度差が大きく残る。
    /// </summary>
    public static Color Plate(int severityRank, bool dark) => severityRank switch
    {
        0 => dark ? Color.FromArgb(0x5B, 0xA3, 0xE0) : Color.FromArgb(0x3B, 0x82, 0xC4),
        1 => dark ? Color.FromArgb(0xF0, 0xA8, 0x30) : Color.FromArgb(0xC7, 0x77, 0x00),
        _ => dark ? Color.FromArgb(0xF0, 0x6A, 0x6E) : Color.FromArgb(0xC2, 0x28, 0x2D),
    };

    /// <summary>エラー・取得中は無彩色。severity 色と混同させない。</summary>
    public static Color NeutralPlate(bool dark) =>
        dark ? Color.FromArgb(0x8A, 0x8A, 0x8A) : Color.FromArgb(0x6E, 0x6E, 0x6E);

    /// <summary>板の明度から文字色を決める。板が明るければ黒、暗ければ白。</summary>
    public static Color InkOn(Color plate)
    {
        // ITU-R BT.601 の輝度
        double luma = (0.299 * plate.R + 0.587 * plate.G + 0.114 * plate.B) / 255.0;
        return luma > 0.6 ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
    }
}
