using System.Net;
using System.Text.Json;
using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Core;

internal sealed class UsageUnauthorizedException() : Exception("401 Unauthorized");

internal sealed class UsageHttpException(HttpStatusCode status, TimeSpan? retryAfter)
    : Exception($"HTTP {(int)status} {status}")
{
    public HttpStatusCode Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class UsageEndpointClient(Uri endpoint)
{
    public const string DefaultEndpoint = "https://api.anthropic.com/api/oauth/usage";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    })
    {
        // タイムアウトは CTS 側で制御する。HttpClient.Timeout だと TaskCanceledException に
        // なってユーザーキャンセルと区別できない。
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>同時リクエストを絶対に投げない（429 対策）。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ServerUsage> FetchAsync(string accessToken, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);

            // ★ DefaultRequestHeaders には入れない。常駐オブジェクトにトークンを残さないため。
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation(
                "User-Agent", $"claude-cli/{ClaudeVersion.Detect()} (external, cli)");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var res = await Http.SendAsync(
                req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

            if (res.StatusCode == HttpStatusCode.Unauthorized)
                throw new UsageUnauthorizedException();

            if (!res.IsSuccessStatusCode)
                throw new UsageHttpException(res.StatusCode, res.Headers.RetryAfter?.Delta);

            string body = await res.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Parse(body);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static ServerUsage Parse(string body)
    {
        UsageResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<UsageResponseDto>(body);
        }
        catch (JsonException ex)
        {
            SaveErrorBody(body);
            throw new InvalidOperationException("使用量レスポンスの JSON を解析できませんでした。", ex);
        }

        var rows = (dto?.Limits ?? [])
            .Select(LimitRow.From)
            .OfType<LimitRow>()
            .ToList();

        // 生 JSON は常に残す。スキーマが変わったとき diff が取れる唯一の手がかり。
        SaveLastUsage(body);

        return new ServerUsage(DateTimeOffset.UtcNow, rows);
    }

    private static void SaveLastUsage(string body) =>
        WriteAtomic(Path.Combine(AppPaths.CacheDir, "last-usage.json"), body);

    private static void SaveErrorBody(string body) =>
        // レスポンスボディのみ。リクエストヘッダは絶対に保存しない（トークンが載る）。
        WriteAtomic(Path.Combine(AppPaths.CacheDir, "last-error-body.txt"), body);

    private static void WriteAtomic(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"キャッシュを書けませんでした: {Path.GetFileName(path)} ({ex.GetType().Name})");
        }
    }
}
