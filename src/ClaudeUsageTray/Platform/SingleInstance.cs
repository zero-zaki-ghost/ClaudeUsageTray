namespace ClaudeUsageTray.Platform;

// =============================================================================
//  二重起動の防止
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  見た目の問題（パネルが 2 枚重なる）だけでなく、レート制限の問題でもある。
//  2 つ動けばエンドポイントを叩く回数も 2 倍になり、本体が守っている 60 秒
//  スロットルの前提を崩す。行儀の良さは「1 プロセスであること」を含んでいる。
//
//  既に動いている場合はダイアログを出さずに黙って終わる。自動起動と手動起動が
//  重なったときに毎回警告が出るのは煩わしいだけで、ユーザーにできることも無い。
// =============================================================================
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private bool _owned;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _owned = true;
    }

    /// <summary>取得できなければ null（＝既に別インスタンスが動いている）。</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\ClaudeUsageTray", out bool createdNew);
        if (createdNew) return new SingleInstance(mutex);

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* 別スレッドが所有していた場合。無視してよい */ }
            _owned = false;
        }
        _mutex.Dispose();
    }
}
