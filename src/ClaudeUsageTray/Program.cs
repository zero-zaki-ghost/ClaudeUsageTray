using System.Windows.Forms;
using ClaudeUsageTray.Config;
using ClaudeUsageTray.Platform;

namespace ClaudeUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var single = SingleInstance.TryAcquire();
        if (single is null)
        {
            // 既に起動している。黙って終わる（ダイアログは出さない）。
            return;
        }

        var options = Options.Parse(args);

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) =>
            Log.Error("UI スレッドで未処理例外", e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("未処理例外", e.ExceptionObject as Exception);

        using var context = new TrayAppContext(options);
        Application.Run(context);
    }
}
