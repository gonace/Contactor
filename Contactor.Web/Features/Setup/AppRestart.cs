namespace Contactor.Web.Features.Setup;

/// <summary>
/// Restarts the app after the setup guide chose another model of board, which decides the services the app starts
/// with. The app only stops: on the Raspberry Pi compose starts it again at once (<c>restart: unless-stopped</c>),
/// and the page reconnects by itself. Under Aspire or <c>dotnet run</c> it has to be started again by hand.
/// </summary>
public sealed class AppRestart
{
    // Long enough for the page to show that the app restarts before its connection ends.
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _time;

    public AppRestart(IHostApplicationLifetime lifetime, TimeProvider time)
    {
        _lifetime = lifetime;
        _time = time;
    }

    /// <summary>Stops the app in a moment; returns at once.</summary>
    public void Soon()
    {
        _ = StopLaterAsync();
    }

    private async Task StopLaterAsync()
    {
        await Task.Delay(Delay, _time);
        _lifetime.StopApplication();
    }
}
