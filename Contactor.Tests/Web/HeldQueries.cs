using System.Data.Common;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// Holds the app's database queries whose SQL matches until the test releases them, so a test can close a page while
/// its load waits for the database. Nothing is held until <see cref="Hold"/>.
/// </summary>
internal sealed class HeldQueries : DbCommandInterceptor
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<string, bool>? _match;
    private int _holds;

    /// <summary>How many queries were held: none means the match found nothing to hold.</summary>
    public int Holds => Volatile.Read(ref _holds);

    /// <summary>The services for an app whose queries this holds.</summary>
    public void AddTo(IServiceCollection services)
    {
        services.ConfigureDbContext<ContactorDbContext>(options => options.AddInterceptors(this));
    }

    /// <summary>From now on, holds each query whose SQL matches until <see cref="Release"/>.</summary>
    public void Hold(Func<string, bool> match)
    {
        Volatile.Write(ref _match, match);
    }

    /// <summary>Lets every held query go on, and holds none from now on.</summary>
    public void Release()
    {
        Volatile.Write(ref _match, null);
        _released.TrySetResult();
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _match) is { } match && match(command.CommandText))
        {
            Interlocked.Increment(ref _holds);
            await _released.Task.WaitAsync(cancellationToken);
        }

        return result;
    }
}
