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
    private static SingleInstance? _current;

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
        if (createdNew)
        {
            _current = new SingleInstance(mutex);
            return _current;
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// ★ 別プロセスに常駐を引き継ぐ前に、必ず先に手放すこと。
    ///
    /// 握ったまま新しいプロセスを起動すると、新プロセスは起動直後の
    /// <see cref="TryAcquire"/> に失敗し「既に動いている」と判断して黙って終了する。
    /// その後こちらも終了するので、**常駐が 1 つも残らない**。
    /// 実際にメニューからの自動起動有効化でこれを踏んだ。
    ///
    /// 保持していない場合（--install など）は何もしない。
    /// </summary>
    public static void ReleaseCurrent()
    {
        _current?.Dispose();
        _current = null;
    }

    /// <summary>ReleaseCurrent と Program.Main の using で二重に呼ばれるので、冪等にしてある。</summary>
    public void Dispose()
    {
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* 別スレッドが所有していた場合。無視してよい */ }
            _owned = false;
            _mutex.Dispose();
        }

        if (ReferenceEquals(_current, this)) _current = null;
    }
}
