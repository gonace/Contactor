using System.Data.Common;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// Makes the app's database queries whose SQL matches throw, so a test can see what a page does when its database read
/// fails. Nothing fails until <see cref="Fail"/>.
/// </summary>
internal sealed class FailingQueries : DbCommandInterceptor
{
    private Func<string, bool>? _match;
    private Func<Exception>? _exception;
    private int _failures;

    /// <summary>How many queries failed: none means the match found nothing to fail.</summary>
    public int Failures => Volatile.Read(ref _failures);

    /// <summary>The services for an app whose queries this fails.</summary>
    public void AddTo(IServiceCollection services)
    {
        services.ConfigureDbContext<ContactorDbContext>(options => options.AddInterceptors(this));
    }

    /// <summary>From now on, each query whose SQL matches throws the exception <paramref name="exception"/> makes.</summary>
    public void Fail(Func<string, bool> match, Func<Exception> exception)
    {
        Volatile.Write(ref _exception, exception);
        Volatile.Write(ref _match, match);
    }

    /// <summary>Lets every query go on from now on.</summary>
    public void Stop()
    {
        Volatile.Write(ref _match, null);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ThrowIfMatched(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfMatched(command);
        return ValueTask.FromResult(result);
    }

    private void ThrowIfMatched(DbCommand command)
    {
        if (Volatile.Read(ref _match) is { } match && match(command.CommandText) && Volatile.Read(ref _exception) is { } exception)
        {
            Interlocked.Increment(ref _failures);
            throw exception();
        }
    }
}
