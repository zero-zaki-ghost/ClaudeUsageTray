using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTray.Config;

internal sealed class OverlaySettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.82;

    [JsonPropertyName("fontSizePx")] public float FontSizePx { get; set; } = 13f;

    [JsonPropertyName("hideOnFullscreen")] public bool HideOnFullscreen { get; set; } = true;

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
