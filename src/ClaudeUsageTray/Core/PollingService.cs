using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Core;

/// <summary>
/// Phase 1 のポーリング。単純な固定間隔ループ。
/// （401 の ShortWatch・指数バックオフ・resets_at 前倒しは Phase 2 で足す）
///
/// ★ 間隔の下限は 60 秒でハードクランプする。Claude Code 本体がキャッシュ実装で
///   60 秒のスロットルをかけている以上、それより速く叩くのは礼儀違反であり
///   429 のリスクでもある。設定でも 60 秒未満は許可しない。
/// </summary>
internal sealed class PollingService : IDisposable
{
    public const int MinIntervalSeconds = 60;

    private readonly UsageEndpointClient _client;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _jitter = new();
    private Task? _loop;

    public event Action<AppState>? StateChanged;

    public AppState Current { get; private set; } = AppState.Initial;

    public PollingService(Uri endpoint, int intervalSeconds)
    {
        _client = new UsageEndpointClient(endpoint);
        _interval = TimeSpan.FromSeconds(Math.Max(MinIntervalSeconds, intervalSeconds));
    }

    public void Start() => _loop = Task.Run(RunAsync);

    /// <summary>メニューの「今すぐ更新」用。</summary>
    public void RequestRefresh() => _ = Task.Run(() => PollOnceAsync(_cts.Token));

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await PollOnceAsync(_cts.Token).ConfigureAwait(false);

            try
            {
                await Task.Delay(NextDelay(), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan NextDelay()
    {
        // ±10% のジッタ。複数インスタンスや PC 復帰時に時刻が揃うのを避ける。
        double factor = 0.9 + _jitter.NextDouble() * 0.2;
        return TimeSpan.FromSeconds(_interval.TotalSeconds * factor);
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            // ★ 毎回読み直す。本体がトークンを更新して書き戻したら自動で追従する。
            var creds = CredentialsReader.Read();

            var usage = await _client.FetchAsync(creds.AccessToken, ct).ConfigureAwait(false);

            Publish(usage.RateLimitsAvailable
                ? new AppState(usage, FetchStatus.Ok, null)
                : new AppState(usage, FetchStatus.NotAvailable, "プランリミットの情報がありません。"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 終了中
        }
        catch (UsageUnauthorizedException)
        {
            Log.Warn("401 を受け取りました。Claude Code 本体がトークンを更新するのを待ちます。");
            Publish(Current with
            {
                Status = FetchStatus.AuthRequired,
                StatusDetail = "Claude Code を起動してサインインし直してください。",
            });
        }
        catch (CredentialsUnavailableException ex)
        {
            Publish(Current with { Status = FetchStatus.AuthRequired, StatusDetail = ex.Message });
        }
        catch (UsageHttpException ex)
        {
            Log.Error($"使用量の取得に失敗しました ({ex.Message})");
            Publish(Current with { Status = FetchStatus.Offline, StatusDetail = ex.Message });
        }
        catch (Exception ex)
        {
            Log.Error("使用量の取得に失敗しました", ex);
            Publish(Current with
            {
                Status = FetchStatus.Offline,
                StatusDetail = $"{ex.GetType().Name}: {Log.ShortMessage(ex)}",
            });
        }
    }

    private void Publish(AppState state)
    {
        Current = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* 終了時の例外は無視 */ }
        _cts.Dispose();
    }
}
