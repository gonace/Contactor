using Contactor.Web.Features.Languages;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Tailscale;

/// <summary>Tailscale's settings: the auth key the app joins with, and its name on the Tailscale network.</summary>
/// <param name="AuthKey">The auth key, or empty when none is saved. A secret: never logged or shown.</param>
/// <param name="HostName">The app's name on the Tailscale network.</param>
public sealed record TailscaleNetworkSettings(string AuthKey, string HostName);

/// <summary>
/// Tailscale's settings while the app runs, as the setup guide saved them (<see cref="TailscaleSetting"/>). A change
/// takes effect at once: Tailscale starts again with it.
/// </summary>
public sealed partial class TailscaleSettings : IDisposable
{
    // How every Tailscale auth key starts.
    private const string AuthKeyPrefix = "tskey-";

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly ILogger<TailscaleSettings> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TailscaleNetworkSettings _current = new("", TailscaleSetting.DefaultHostName);

    public TailscaleSettings(IDbContextFactory<ContactorDbContext> dbFactory, ILogger<TailscaleSettings> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>The settings changed: Tailscale starts again with them.</summary>
    public event EventHandler? Changed;

    public TailscaleNetworkSettings Current => Volatile.Read(ref _current);

    /// <summary>At start: reads what the setup guide saved.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.TailscaleSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        Volatile.Write(ref _current, Combine(row));
    }

    /// <summary>
    /// Saves the auth key and the app's name, and starts Tailscale again with them when they changed. Returns what is
    /// wrong in plain words, and saves nothing then.
    /// </summary>
    public async Task<UiText?> SaveAsync(string? authKey, string? hostName, CancellationToken cancellationToken)
    {
        authKey = authKey?.Trim() ?? "";
        hostName = hostName?.Trim().ToLowerInvariant() ?? "";
        var problem = Check(authKey, hostName);
        if (problem is not null)
        {
            return problem;
        }

        bool changed;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.TailscaleSettings.SingleOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                row = new TailscaleSetting();
                db.TailscaleSettings.Add(row);
            }

            // Compared under the lock, so two saves at once can't both miss a change.
            var before = Current;
            (row.AuthKey, row.HostName) = (authKey, hostName);
            await db.SaveChangesAsync(cancellationToken);
            Volatile.Write(ref _current, Combine(row));
            changed = before != Current;
        }
        finally
        {
            _gate.Release();
        }

        LogSaved(hostName);
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return null;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>What is wrong with Tailscale's settings, or null; a page puts it in words.</summary>
    public static UiText? Check(string authKey, string hostName)
    {
        ArgumentNullException.ThrowIfNull(authKey);
        ArgumentNullException.ThrowIfNull(hostName);
        if (!authKey.StartsWith(AuthKeyPrefix, StringComparison.Ordinal)
            || authKey.Length <= AuthKeyPrefix.Length
            || authKey.Length > TailscaleSetting.AuthKeyMaxLength
            || authKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return UiText.Of<SetupTexts>("Setup.Tailscale.AuthKey");
        }

        // One DNS label: the first part of the app's address on the Tailscale network.
        if (hostName.Length == 0
            || hostName.Length > TailscaleSetting.HostNameMaxLength
            || hostName.StartsWith('-')
            || hostName.EndsWith('-')
            || !hostName.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            return UiText.Of<SetupTexts>("Setup.Tailscale.Name");
        }

        return null;
    }

    private static TailscaleNetworkSettings Combine(TailscaleSetting? row)
    {
        return new TailscaleNetworkSettings(row?.AuthKey ?? "", row?.HostName ?? TailscaleSetting.DefaultHostName);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tailscale saved as {HostName}")]
    private partial void LogSaved(string hostName);
}
