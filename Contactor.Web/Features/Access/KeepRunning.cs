namespace Contactor.Web.Features.Access;

/// <summary>
/// Keeps a program the app runs itself running, e.g. cloudflared or tailscaled: runs it, and whenever it stops or fails
/// to start, runs it again after a growing wait (1 s, doubling to at most 1 min). A run that lasted a while starts the
/// waits over. The image has no shell or process manager, so the app looks after these programs itself.
/// </summary>
public static class KeepRunning
{
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(1);

    // A run this long counts as having worked: the next wait starts from the first again.
    private static readonly TimeSpan RanLongEnough = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs <paramref name="runOnce"/> until cancelled. After each run, <paramref name="stopped"/> gets its exit code,
    /// or the exception it failed with, and the wait before the next.
    /// </summary>
    public static async Task RunAsync(
        Func<CancellationToken, Task<int>> runOnce,
        TimeProvider time,
        Action<int?, Exception?, TimeSpan> stopped,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runOnce);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(stopped);
        var wait = FirstWait;
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = time.GetUtcNow();
            int? exitCode = null;
            Exception? failure = null;
            try
            {
                exitCode = await runOnce(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A program missing or failing to start must not stop the app; it's reported and tried again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }

            if (time.GetUtcNow() - started >= RanLongEnough)
            {
                wait = FirstWait;
            }

            stopped(exitCode, failure, wait);
            try
            {
                await Task.Delay(wait, time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestWait.Ticks));
        }
    }
}
