using System.Diagnostics;
using System.Windows.Forms;
using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Platform;

// =============================================================================
//  配置と自動起動の登録
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. インストーラは作らない。
//     個人用ツールに MSI や MSIX は過剰で、署名やアンインストール情報の管理まで
//     抱え込むことになる。自分自身を %LOCALAPPDATA% にコピーして Run キーを
//     書くだけで足りる。
//
//  2. それでも「固定の場所」は要る。
//     自動起動はレジストリに exe のフルパスを書く。bin\Debug やダウンロード
//     フォルダのままだと、ビルドし直したり整理した時点で壊れ、しかも壊れたことに
//     気づけない。だから配置と登録をセットにしている。
//
//  3. %LOCALAPPDATA% を選ぶ理由。
//     昇格が不要で、ユーザーごとに分かれ、Defender の誤検知も少ない。
//     Program Files は昇格が要る。
//
//  4. ディレクトリごとコピーする。
//     publish 済みの単一 exe なら 1 ファイルで済むが、Debug ビルドから試すことも
//     あるので依存 DLL ごと持っていく。判定を増やすより常に丸ごとコピーする
//     ほうが壊れにくい。
// =============================================================================
internal static class Installer
{
    public static string InstallDir => Path.Combine(AppPaths.AppDataDir, "app");

    public static string InstalledExe => Path.Combine(InstallDir, "ClaudeUsageTray.exe");

    private static string CurrentExe =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsRunningFromInstallDir() =>
        string.Equals(Path.GetDirectoryName(CurrentExe)?.TrimEnd('\\'),
                      InstallDir.TrimEnd('\\'),
                      StringComparison.OrdinalIgnoreCase);

    public static void Install()
    {
        try
        {
            StopOtherInstances();

            string sourceDir = Path.GetDirectoryName(CurrentExe)!;

            if (!IsRunningFromInstallDir())
            {
                Directory.CreateDirectory(InstallDir);

                // publish 済みなら単一 exe だけだが、Debug ビルドからでも動くよう
                // ディレクトリごとコピーする
                foreach (string src in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(sourceDir, src);
                    string dst = Path.Combine(InstallDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, overwrite: true);
                }
            }

            AutoStart.Enable(InstalledExe);

            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });

            MessageBox.Show(
                $"インストールしました。\n\n" +
                $"配置先:\n{InstallDir}\n\n" +
                $"Windows 起動時に自動で立ち上がります。\n" +
                $"解除するにはパネルを右クリックして\n" +
                $"「Windows 起動時に開始」のチェックを外してください。",
                "ClaudeUsageTray", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error("インストールに失敗しました", ex);
            MessageBox.Show($"インストールに失敗しました。\n\n{ex.GetType().Name}: {Log.ShortMessage(ex)}",
                "ClaudeUsageTray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static void Uninstall()
    {
        try
        {
            AutoStart.Disable();
            StopOtherInstances();

            bool deferred = false;

            if (Directory.Exists(InstallDir))
            {
                if (IsRunningFromInstallDir())
                {
                    // ★ 自分自身が入っているフォルダは、自分が動いている間は消せない。
                    //   案内どおりに配置先の exe を直接叩くと必ずこの経路に入るので、
                    //   外部プロセスに「少し待ってから消す」よう頼んで抜ける。
                    ScheduleSelfDelete();
                    deferred = true;
                }
                else
                {
                    Directory.Delete(InstallDir, recursive: true);
                }
            }

            MessageBox.Show(
                "自動起動を解除しました。\n\n" +
                (deferred
                    ? $"配置したファイルは、このウィンドウを閉じた直後に削除されます:\n{InstallDir}\n\n"
                    : $"配置したファイルを削除しました:\n{InstallDir}\n\n") +
                $"設定とログは残しています:\n{AppPaths.AppDataDir}",
                "ClaudeUsageTray", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error("アンインストールに失敗しました", ex);
            MessageBox.Show($"アンインストールに失敗しました。\n\n{ex.GetType().Name}: {Log.ShortMessage(ex)}",
                "ClaudeUsageTray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 自分が入っているフォルダを、自分の終了後に消してもらう。
    ///
    /// cmd に「少し待つ → フォルダごと削除」を投げて、こちらは普通に終了する。
    /// ウィンドウを出さないよう CreateNoWindow で起動する。
    /// </summary>
    private static void ScheduleSelfDelete()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // timeout は待ち、rd はフォルダごと削除。&& ではなく & で繋ぎ、
                // timeout が失敗しても削除は試みる。
                Arguments = $"/c timeout /t 3 /nobreak > nul & rd /s /q \"{InstallDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"配置先の削除を予約できませんでした。手動で削除してください。({ex.GetType().Name})");
        }
    }

    /// <summary>コピー先の exe が実行中だと上書きできないので先に止める。</summary>
    private static void StopOtherInstances()
    {
        int self = Environment.ProcessId;

        foreach (var p in Process.GetProcessesByName("ClaudeUsageTray"))
        {
            using (p)
            {
                if (p.Id == self) continue;

                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log.Warn($"既存のプロセスを終了できませんでした。({ex.GetType().Name})");
                }
            }
        }
    }
}
