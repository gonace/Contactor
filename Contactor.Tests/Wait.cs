namespace Contactor.Tests;

/// <summary>
/// Waits, in any test, for something another thread does, e.g. the poller's loop or the simulated board. The clocks
/// the tests drive are fake, so nothing here waits for time to pass: the deadline only stops a test that would hang.
/// </summary>
internal static class Wait
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
