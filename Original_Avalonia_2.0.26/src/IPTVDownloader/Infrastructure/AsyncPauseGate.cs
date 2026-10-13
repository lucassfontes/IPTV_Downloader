namespace IPTVDownloader.Infrastructure;

public sealed class AsyncPauseGate
{
    private volatile TaskCompletionSource<bool>? _paused;
    public bool IsPaused => _paused is not null;

    public void Pause()
    {
        Interlocked.CompareExchange(
            ref _paused,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            null);
    }

    public void Resume()
    {
        var tcs = Interlocked.Exchange(ref _paused, null);
        tcs?.TrySetResult(true);
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        var tcs = _paused;
        if (tcs is null) return;
        await tcs.Task.WaitAsync(ct);
    }
}
