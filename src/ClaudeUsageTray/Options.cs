using ClaudeUsageTray.Core;

namespace ClaudeUsageTray;

internal sealed record Options(Uri Endpoint, int IntervalSeconds, bool IconStress)
{
    public static Options Parse(string[] args)
    {
        var endpoint = new Uri(UsageEndpointClient.DefaultEndpoint);
        int interval = 120;
        bool stress = false;

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

                // GDI ハンドルリークの加速テスト用
                case "--icon-stress":
                    stress = true;
                    break;
            }
        }

        // ★ 設定でも 60 秒未満は許可しない
        interval = Math.Max(PollingService.MinIntervalSeconds, interval);

        return new Options(endpoint, interval, stress);
    }
}
