using System.Diagnostics;
using System.Text.Json;
using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Core;

// =============================================================================
//  Claude Code 本体が動いているか
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  1. なぜ要るのか。
//     **トークンを更新できるのは本体だけ**なので、本体が 1 つも動いていなければ
//     失効したトークンは絶対に更新されない。これは端末の時計にも `expiresAt` にも
//     依存しない、外から観測できる確実な事実になる。
//
//     失効判定を `expiresAt`（＝時計との突き合わせ）だけに頼ると、時計がずれた
//     端末で「生きているトークンを失効と誤判定して永久に沈黙する」危険がある。
//     本体の生死という別軸の材料があれば、その判定を裏から支えられる。
//
//  2. ★ 「本体が動いていない ＝ 取りに行かなくていい」ではない。
//     週次リミットはアカウント単位なので、**別の端末や claude.ai の利用でも
//     数字は動く**。手元で本体が止まっていても値が変わりうる以上、
//     取得を止めてはいけない。**間隔を伸ばすだけ**にする。
//     ここを取り違えると「PC で Claude Code を閉じている間、週次が更新されない」
//     という分かりにくい不具合になる。
//
//  3. プロセス名では判定しない。
//     `claude` という名前のプロセスは他にもあり得る。本体が自分で書いている
//     `~/.claude/sessions/<pid>.json` の pid を見て、その pid が生きているかを
//     確かめる。**本体が名乗っている情報を正とする。**
//
//  4. 読めなければ「動いている」と答える。
//     判定に失敗したときに「動いていない」と答えると、間隔が伸び、
//     失効ガードも強く効いてしまう。**分からないときは安全側（従来どおり）に倒す。**
// =============================================================================
internal static class ClaudeProcess
{
    /// <summary>
    /// これより古い session ファイルは見ない。本体が落ちるとファイルは残るので、
    /// pid の生存確認だけだと「別プロセスが同じ pid を再利用した」場合に誤判定する。
    /// </summary>
    private static readonly TimeSpan SessionFreshness = TimeSpan.FromHours(12);

    /// <summary>毎ポーリングで数ファイル読む程度だが、短時間の連続呼び出しは束ねる。</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(20);

    private static bool _cached = true;
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private static readonly Lock Gate = new();

    /// <summary>本体が 1 つでも動いていれば true。判定できなければ true（安全側）。</summary>
    public static bool AnyRunning()
    {
        lock (Gate)
        {
            if (DateTimeOffset.UtcNow - _cachedAt < CacheFor) return _cached;

            _cached = Detect();
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
    }

    private static bool Detect()
    {
        try
        {
            var dir = new DirectoryInfo(AppPaths.SessionsDir);
            if (!dir.Exists) return false;   // ディレクトリごと無いなら本体は未使用

            var cutoff = DateTime.UtcNow - SessionFreshness;

            foreach (var file in dir.EnumerateFiles("*.json"))
            {
                if (file.LastWriteTimeUtc < cutoff) continue;
                if (ReadPid(file) is { } pid && IsAlive(pid)) return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // 4 のとおり、分からないときは「動いている」と答える。
            Log.Warn($"本体の起動状態を判定できませんでした。({ex.GetType().Name})");
            return true;
        }
    }

    private static int? ReadPid(FileInfo file)
    {
        try
        {
            // 本体が書いている最中かもしれないので、ロックせず共有で開く。
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);

            return doc.RootElement.TryGetProperty("pid", out var p) && p.TryGetInt32(out int pid)
                ? pid
                : null;
        }
        catch
        {
            // 壊れた 1 ファイルで全体の判定を落とさない。
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            // そんな pid は無い＝終了済み
            return false;
        }
        catch
        {
            // 権限などで見られないだけかもしれないので、生きている扱いにする。
            return true;
        }
    }
}
