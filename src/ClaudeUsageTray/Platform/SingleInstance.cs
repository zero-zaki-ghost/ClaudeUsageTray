namespace ClaudeUsageTray.Platform;

/// <summary>
/// 二重起動を防ぐ。トレイに同じアイコンが 2 つ並ぶのを避けるためだけでなく、
/// 使用量エンドポイントへの同時リクエストを防ぐ意味もある（429 対策）。
/// </summary>
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
