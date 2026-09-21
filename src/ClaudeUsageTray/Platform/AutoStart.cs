using Microsoft.Win32;
using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Platform;

// =============================================================================
//  自動起動
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. HKCU の Run キーを使う。タスクスケジューラは使わない。
//     Run キーなら昇格が要らず、しかも「設定 > アプリ > スタートアップ」に項目
//     として現れ、ユーザーが自分で止められる。勝手に居座らないこと。
//     タスクスケジューラは隠れていて気づかれにくく、常駐ツールには不適切。
//
//  2. StartupApproved も見る。
//     Run キーに値があっても、ユーザーが設定画面で無効にしていれば起動しない。
//     Run キーの有無だけを見てチェックを付けると「有効なのに起動しない＝壊れて
//     いる」ように見える。OS 側の状態を正として表示する。
// =============================================================================
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>「設定 &gt; スタートアップ」でユーザーが無効化したかどうかが入っている。</summary>
    private const string ApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public const string ValueName = "ClaudeUsageTray";

    /// <summary>
    /// Run キーに登録されていて、かつユーザーが設定画面で無効化していない状態だけを
    /// 「有効」と見なす。Run キーの有無だけを見ると、設定画面で切ったのに
    /// チェックが付いたままになり「壊れている」ように見える。
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } blob)
            {
                // 先頭バイトの下位ビットが 1 なら「ユーザーが無効化した」
                if ((blob[0] & 0x01) != 0) return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"自動起動の状態を読めませんでした。({ex.GetType().Name})");
            return false;
        }
    }

    public static void Enable(string exePath)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            run.SetValue(ValueName, $"\"{exePath}\" --startup", RegistryValueKind.String);

            // 過去に設定画面で無効化されていると Run キーだけ書いても起動しないので解除する
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);

            Log.Info($"自動起動を有効にしました: {exePath}");
        }
        catch (Exception ex)
        {
            Log.Error("自動起動を有効にできませんでした", ex);
        }
    }

    public static void Disable()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("自動起動を無効にしました。");
        }
        catch (Exception ex)
        {
            Log.Error("自動起動を無効にできませんでした", ex);
        }
    }
}
