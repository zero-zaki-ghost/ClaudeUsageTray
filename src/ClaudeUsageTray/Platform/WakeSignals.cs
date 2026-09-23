using System.Net.NetworkInformation;
using Microsoft.Win32;
using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Platform;

// =============================================================================
//  「今すぐ取り直したほうがいい」と分かる瞬間を拾う
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. ★ 判定には使わない。起こすためだけに使う。
//     NetworkInterface.GetIsNetworkAvailable() は信用できない。VPN アダプタ、
//     Hyper-V の仮想スイッチ、WSL の仮想 NIC が「接続あり」を返すので、
//     実際には繋がらないのに true になる。逆もある。
//
//     **これで送信を止めると、繋がるのに沈黙するアプリになる。**
//     しかも理由は画面に出ない。expiresAt を時計と突き合わせて永久に黙る危険と
//     まったく同じ構図で、逃げ道をまた 1 つ作る羽目になる。
//
//     だからここは「抑制」には一切使わず、**起床のきっかけとしてだけ**使う。
//     誤検出しても「余計に 1 回取りに行く」だけで済み、新しい失敗モードを作らない。
//
//  2. これがあるから、バックオフの上限を伸ばせる。
//     上限が 600 秒で止まっていたのは、**復帰に気づく手段がポーリングしか
//     なかった**から。長くするとその分だけ復帰が遅れる。
//     起床シグナルがあれば復帰は数秒で拾えるので、上限を 30 分まで伸ばしても
//     体感は悪くならない。8 時間オフラインでの試行回数は 233 → 約 16 回になる。
//     **回数と復帰速度が同時に良くなる。**
//
//  3. 束ねて間引く。
//     NetworkAddressChanged はアダプタごとに何度も飛ぶ。そのまま流すと
//     1 回の再接続で何度も起こされる。ここで 10 秒に 1 回へ間引く。
//     加えて送信間隔の下限が UsageEndpointClient にあるので、
//     仮に間引きを抜けても 60 秒より速くは飛ばない（二重の歯止め）。
//
//  4. 静的イベントは必ず外す。
//     SystemEvents の購読は静的フィールドに残るため、Dispose で外さないと
//     このオブジェクトが永久に回収されない。
// =============================================================================
internal sealed class WakeSignals : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private DateTimeOffset _lastRaised = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>引数は理由（ログ用）。</summary>
    public event Action<string>? Woke;

    public WakeSignals()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Raise("ネットワーク構成の変化");

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        // 「切れた」では起こさない。取りに行っても無駄なので。
        if (e.IsAvailable) Raise("ネットワーク復帰");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Raise("スリープ復帰");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            Raise("ロック解除");
    }

    private void Raise(string reason)
    {
        lock (_gate)
        {
            if (_disposed) return;

            var now = DateTimeOffset.UtcNow;
            if (now - _lastRaised < Debounce) return;
            _lastRaised = now;
        }

        Log.Info($"起床シグナル: {reason}");

        // ハンドラ側の例外でこちらが死なないようにする。OS のイベントスレッドで
        // 走るので、ここで落ちると原因が追いにくい場所に飛ぶ。
        try { Woke?.Invoke(reason); }
        catch (Exception ex) { Log.Warn($"起床の通知に失敗しました。({ex.GetType().Name})"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;

        Woke = null;
    }
}
