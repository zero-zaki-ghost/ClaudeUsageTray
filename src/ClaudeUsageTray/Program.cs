using System.Windows.Forms;
using ClaudeUsageTray.Config;
using ClaudeUsageTray.Platform;

namespace ClaudeUsageTray;

// =============================================================================
//  エントリポイント
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  ここでは「起動するかしないか」だけを決め、組み立ては OverlayAppContext に
//  任せる。順序に意味があるのは次の 2 点。
//
//  ・--install / --uninstall は常駐しない一回きりの処理なので、単一インスタンスの
//    Mutex を取る前に処理して抜ける。取ってしまうとインストール先の新しい
//    プロセスが起動できない。
//
//  ・未処理例外はログに残すだけで、ダイアログは出さずプロセスも落とさない。
//    常駐アプリが勝手に消えるのが最も困る。数字が止まっていることはパネルの
//    「古さ」表示で分かる。
// =============================================================================
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var options = Options.Parse(args);

        ApplicationConfiguration.Initialize();

        // インストール／アンインストールは常駐しないので Mutex を取る前に処理する
        if (options.Install) { Installer.Install(); return; }
        if (options.Uninstall) { Installer.Uninstall(); return; }

        using var single = SingleInstance.TryAcquire();
        if (single is null)
        {
            // 既に起動している。黙って終わる（ダイアログは出さない）。
            return;
        }

        Application.ThreadException += (_, e) =>
            Log.Error("UI スレッドで未処理例外", e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("未処理例外", e.ExceptionObject as Exception);

        using var context = new OverlayAppContext(options);
        Application.Run(context);
    }
}
