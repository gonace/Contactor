using Contactor.Boards.Vm201;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Contactor.Tests.Web;

/// <summary>The board connection the app keeps open, against the simulator over loopback.</summary>
public sealed class BoardHostedServiceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ConnectToTheSavedAddress_FromTheFirstConnection()
    {
        await using var fromSettings = new Vm201Simulator();
        fromSettings.Start();
        await using var saved = new Vm201Simulator();
        saved.Start();
        await using var database = TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddLocalClock(configuration);
                services.AddBoards(configuration);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddEvents(configuration);
            },
            new Dictionary<string, string?>
            {
                ["Vm201:Host"] = "127.0.0.1",
                ["Vm201:Port"] = fromSettings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Vm201:DisableBoardTimers"] = "false",
            });
        await database.Services.InitializeDatabaseAsync(Cancel);
        await database.Services.UseBoardAsync("vm201");
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var board = await db.Boards.SingleAsync(Cancel);
            (board.Host, board.Port) = ("127.0.0.1", saved.Port);
            await db.SaveChangesAsync(Cancel);
        }

        var client = database.Services.BoardService<Vm201Client>();
        var service = database.Services.GetServices<IHostedService>().OfType<BoardHostedService>().Single();
        await service.StartAsync(Cancel);
        try
        {
            await WaitUntilAsync(() => client.State.IsConnected);
        }
        finally
        {
            await service.StopAsync(Cancel);
        }

        Assert.Equal(1, saved.ConnectionCount);
        Assert.Equal(0, fromSettings.ConnectionCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, Cancel);
        }
    }
}
