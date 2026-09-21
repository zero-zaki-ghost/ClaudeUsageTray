using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTray.Config;

// =============================================================================
//  設定
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. 設定画面は作らない。
//     個人用の常駐ツールで設定ダイアログを作るのは割に合わない。
//     よく変えるものだけ右クリックメニューに出し、残りは JSON を直接編集する。
//     項目を増やしたくなったら、まず「既定値で困る人がいるか」を考える。
//
//  2. 項目を増やさない。
//     しきい値のカスタマイズは持たない（サーバーの severity に従うため不要）。
//     色・フォント名・アイコン画像の差し替えも持たない。設定が増えるほど
//     組み合わせが増え、壊れ方が読めなくなる。
//
//  3. 位置は絶対座標で保存しない。
//     「モニタのデバイス名 + 作業領域に対する相対座標」で持つ。絶対座標だと
//     解像度変更やモニタの抜き差しで画面外に飛び、二度と掴めなくなる。
//
//  4. 書き込みは atomic に。
//     tmp に書いてから Move する。突然のシャットダウンで壊れた JSON を
//     読んで起動できなくなるのを防ぐ。読む側も失敗したら黙って既定値に戻す。
// =============================================================================

internal sealed class OverlaySettings
{
    // ⚠ "enabled" は意図的に持たない。
    //    パネルを隠せるようにすると、トレイが無い以上メニューに辿り着けなくなり、
    //    終了する手段まで失われる（0 章「UI を殺せる設定を置かない」）。
    //    設定項目として存在すると「効くはず」と誤解させるので、定義ごと削除した。

    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.82;

    [JsonPropertyName("fontSizePx")] public float FontSizePx { get; set; } = 13f;

    [JsonPropertyName("hideOnFullscreen")] public bool HideOnFullscreen { get; set; } = true;

    /// <summary>
    /// クリックを下のウィンドウへ素通りさせるか。
    /// 既定は false（＝素通りさせない）。透過していると掴めず、移動もメニューも
    /// ホットキー頼みになって入口が分からなくなるため。
    /// 邪魔なときはメニューから ON にする。
    /// </summary>
    [JsonPropertyName("clickThrough")] public bool ClickThrough { get; set; }

    /// <summary>
    /// 位置は「モニタのデバイス名 + 作業領域に対する相対座標」で持つ。
    /// 絶対座標で保存すると解像度変更やモニタの抜き差しで画面外に飛ぶ。
    /// </summary>
    [JsonPropertyName("monitorDeviceName")] public string? MonitorDeviceName { get; set; }

    /// <summary>
    /// 既定は作業領域の右下。右上だとウィンドウの最小化・最大化・閉じるボタンに
    /// かぶってしまう（クリックは透過するが、ボタンが見えなくなる）。
    /// 作業領域なのでタスクバーの上に載ることもない。
    /// </summary>
    [JsonPropertyName("relativeX")] public double RelativeX { get; set; } = 0.995;

    [JsonPropertyName("relativeY")] public double RelativeY { get; set; } = 0.995;
}

internal sealed class AppSettings
{
    [JsonPropertyName("overlay")] public OverlaySettings Overlay { get; set; } = new();

    private static string FilePath => Path.Combine(AppPaths.AppDataDir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"設定を読めませんでした。既定値を使います。({ex.GetType().Name})");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"設定を保存できませんでした。({ex.GetType().Name})");
        }
    }
}
