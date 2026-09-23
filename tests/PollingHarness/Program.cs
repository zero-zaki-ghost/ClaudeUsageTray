using System.Net;
using System.Net.Sockets;
using System.Text;
using ClaudeUsageTray.Core;

// =============================================================================
//  取得まわりの振る舞いを、本物の通信も本物のトークンも使わずに確かめる
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. なぜ xunit ではないのか。
//     ここで確かめたいのは「何秒待つか」「何通送るか」という**時間の振る舞い**で、
//     1 項目に 1 分前後かかる。単体テストの枠に押し込むより、
//     手で走らせる 1 本の実行ファイルにして終了コードで判定するほうが素直。
//     NuGet 依存ゼロという本体の方針もそのまま保てる。
//
//  2. ★ 本物の認証情報を絶対に使わない。
//     CLAUDE_CONFIG_DIR を一時フォルダへ向け、偽のトークンを置く。
//     スタブは認証を見ないので、これで全シナリオが成立する。
//     以前は実トークンをループバックのスタブへ送っていたが、
//     **検証のために本物の資格情報を動かす必要はどこにも無い。**
//
//  3. スタブはリクエストの中身を読み捨てる。
//     ヘッダを保持しない。保存もしない。万が一この仕組みを
//     実エンドポイント相手に使っても、こちらから漏れる経路を作らない。
//
//  4. HttpListener ではなく TcpListener を使う。
//     HttpListener は URL 予約（netsh http add urlacl）が要る場合があり、
//     昇格なしで動かない環境がある。生の TCP なら確実に動く。
//
//  【使い方】
//     dotnet run --project tests\PollingHarness
//     終了コード 0 = 全項目 PASS
//     所要時間は 3 分ほど（送信間隔の下限 60 秒を実際に待つため）
// =============================================================================

// ------------------------------------------------------------ 偽の認証情報
var home = Path.Combine(Path.GetTempPath(), "cut-harness-home");
Directory.CreateDirectory(home);
Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", home);

void WriteCredentials(DateTimeOffset expiresAt) =>
    File.WriteAllText(Path.Combine(home, ".credentials.json"), $$"""
    { "claudeAiOauth": {
        "accessToken": "FAKE-TOKEN-FOR-LOCAL-TEST-ONLY",
        "expiresAt": {{expiresAt.ToUnixTimeMilliseconds()}},
        "subscriptionType": "team" } }
    """);

// ------------------------------------------------------------ スタブサーバ
var hits = new List<DateTimeOffset>();
var script = new Queue<(int Status, string? RetryAfter)>();
var listener = new TcpListener(IPAddress.Loopback, 5998);
listener.Start();

_ = Task.Run(async () =>
{
    while (true)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(); }
        catch (ObjectDisposedException) { return; }
        catch (SocketException) { return; }

        _ = Task.Run(async () =>
        {
            using (client)
            {
                lock (hits) hits.Add(DateTimeOffset.UtcNow);

                // ★ 読み捨てる。Authorization ヘッダを変数にも残さない。
                //   全部読み切る必要は無い（応答は要求の内容に依存しない）が、
                //   読まずに閉じるとクライアント側が接続リセットとして扱うので 1 回だけ読む。
                var scratch = new byte[8192];
                int read = await client.GetStream().ReadAsync(scratch);
                if (read <= 0) { /* 何も来なくても応答は返す */ }

                (int status, string? retryAfter) =
                    script.Count > 0 ? script.Dequeue() : (200, null);

                string body = status == 200
                    ? """{"limits":[{"kind":"session","group":"session","percent":7,"severity":"normal","is_active":true}]}"""
                    : """{"error":"stub"}""";

                var head = new StringBuilder()
                    .Append($"HTTP/1.1 {status} X\r\n");
                if (retryAfter is not null) head.Append($"Retry-After: {retryAfter}\r\n");
                head.Append("Content-Type: application/json\r\n")
                    .Append($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n")
                    .Append("Connection: close\r\n\r\n")
                    .Append(body);

                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(head.ToString()));
                await client.GetStream().FlushAsync();
            }
        });
    }
});

var endpoint = new Uri("http://127.0.0.1:5998/api/oauth/usage");
int passed = 0, failed = 0;

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    Console.WriteLine($"        {detail}");
    if (ok) passed++; else failed++;
}

void Reset() { lock (hits) hits.Clear(); script.Clear(); }
int Hits() { lock (hits) return hits.Count; }

async Task<bool> WaitForHitAsync(int atLeast, TimeSpan limit)
{
    var deadline = DateTimeOffset.UtcNow + limit;
    while (Hits() < atLeast && DateTimeOffset.UtcNow < deadline)
        await Task.Delay(TimeSpan.FromSeconds(1));
    return Hits() >= atLeast;
}

// ============================================================================
Console.WriteLine("\n[1] 失効しているトークンは送らない");
Console.WriteLine("    実機で 2 回踏んだ「401 を 3 回 → 429」の根治にあたる部分。");
{
    Reset();
    WriteCredentials(DateTimeOffset.UtcNow.AddHours(-3));

    using var service = new PollingService(endpoint, 60);
    AppState? latest = null;
    service.StateChanged += s => latest = s;
    service.Start();

    await Task.Delay(TimeSpan.FromSeconds(8));

    Check("HTTP を 1 通も送らない", Hits() == 0, $"送信 {Hits()} 回（期待 0）");

    Check("AuthRequired になり、案内が出る",
        latest?.Status == FetchStatus.AuthRequired && latest.StatusDetail is { Length: > 0 },
        $"status={latest?.Status} detail=\"{latest?.StatusDetail}\"");

    // 失効しただけなら Claude Code を起動すれば直る。再サインインは不要で、
    // 求めるとユーザーに「何か壊した」と思わせる。
    Check("案内が「サインインし直せ」ではない",
        latest?.StatusDetail?.Contains("サインインし直") != true,
        $"\"{latest?.StatusDetail}\"");
}

// ============================================================================
Console.WriteLine("\n[2] 起床シグナルで待機を打ち切る");
Console.WriteLine("    間隔 600 秒でも、起床すれば下限 60 秒で取りに行く。");
{
    Reset();
    WriteCredentials(DateTimeOffset.UtcNow.AddHours(3));

    using var service = new PollingService(endpoint, 600);
    service.Start();

    bool first = await WaitForHitAsync(1, TimeSpan.FromSeconds(90));
    var wokeAt = DateTimeOffset.UtcNow;
    service.Wake("ハーネス");          // OS イベントと同じ経路

    bool second = await WaitForHitAsync(2, TimeSpan.FromSeconds(100));
    double elapsed = (DateTimeOffset.UtcNow - wokeAt).TotalSeconds;

    Check("初回取得は走っている", first, $"送信 {Hits()} 回");
    Check("起床により 600 秒を待たずに再取得した", second, $"送信 {Hits()} 回（期待 2）");

    // 起床は「早める」ためのものだが、下限だけは破らせない。
    Check("ただし送信間隔の下限 60 秒は割らない",
        !second || elapsed >= 55, $"起床から {elapsed:F0} 秒");
}

// ============================================================================
Console.WriteLine("\n[3] Retry-After の待機は起床では割り込ませない");
Console.WriteLine("    サーバーが待てと言っているものを、こちらの都合で早めない。");
{
    Reset();
    WriteCredentials(DateTimeOffset.UtcNow.AddHours(3));
    script.Enqueue((429, "120"));

    using var service = new PollingService(endpoint, 60);
    service.Start();

    // 下限 60 秒はプロセス単位（static）なので、前の項目の送信から
    // 60 秒経つまで 1 通目が出ない。届いてから起床を試す。
    bool first = await WaitForHitAsync(1, TimeSpan.FromSeconds(90));

    service.Wake("ハーネス");
    await Task.Delay(TimeSpan.FromSeconds(40));   // 120 秒よりずっと手前

    Check("429 を 1 回受けている", first, $"送信 {Hits()} 回（期待 1）");
    Check("起床しても Retry-After を破らない", Hits() == 1, $"送信 {Hits()} 回（期待 1 のまま）");
}

Console.WriteLine($"\n==== PASS {passed} / FAIL {failed} ====");
listener.Stop();
return failed == 0 ? 0 : 1;
