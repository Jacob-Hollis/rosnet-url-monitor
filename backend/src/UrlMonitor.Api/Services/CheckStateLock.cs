namespace UrlMonitor.Api.Services;

/// <summary>
/// App-wide lock around "read URL state → apply check results → save", and around URL deletes.
/// Without it, a scheduled run and a "check now" could both start from the same stale failure count,
/// overwrite each other's updates and raise the same alert twice. Only the short database part is
/// locked; the slow HTTP checks run outside it.
/// </summary>
public sealed class CheckStateLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}
