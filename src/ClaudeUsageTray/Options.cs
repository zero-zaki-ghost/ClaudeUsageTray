using ClaudeUsageTray.Core;

namespace ClaudeUsageTray;

// =============================================================================
//  コマンドライン引数
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  引数は「検証のため」と「配置のため」に限る。常用の設定はここに置かない
//  （設定は settings.json とメニュー）。引数で何でも変えられるようにすると、
//  どの状態で動いているのか分からなくなる。
//
//  --endpoint はローカルのスタブサーバに向けて 401 / 429 / 壊れた JSON などを
//  再現するためのもの。これが無いと異常系の検証が「Wi-Fi を切る」しかできなく
//  なり、テストのサイクルが致命的に遅くなる。
//
//  ★ --interval で 60 秒未満を渡されても 60 秒に丸める。
//    引数だから通す、という例外を作らない。行儀の良さは設定で破れてはいけない。
// =============================================================================
internal sealed record Options(
    Uri Endpoint,
    int IntervalSeconds,
    bool Install,
    bool Uninstall,
    bool FromStartup)
{
    public static Options Parse(string[] args)
    {
        var endpoint = new Uri(UsageEndpointClient.DefaultEndpoint);
        int interval = 120;
        bool install = false, uninstall = false, fromStartup = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                // Phase 1.5 のローカルスタブサーバに向けるため
                case "--endpoint" when i + 1 < args.Length:
                    if (Uri.TryCreate(args[++i], UriKind.Absolute, out var u)) endpoint = u;
                    break;

                case "--interval" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int s)) interval = s;
                    break;

                case "--install":
                    install = true;
                    break;

                case "--uninstall":
                    uninstall = true;
                    break;

                // 自動起動から呼ばれたとき。ログオン直後の I/O 混雑を避けて初回取得を遅らせる。
                case "--startup":
                    fromStartup = true;
                    break;
            }
        }

        // ★ 設定でも 60 秒未満は許可しない
        interval = Math.Max(PollingService.MinIntervalSeconds, interval);

        return new Options(endpoint, interval, install, uninstall, fromStartup);
    }
}
