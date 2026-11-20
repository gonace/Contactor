using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The Debugging setting on Settings, the Debug tab it adds for that person, and the Debug screen.
/// </summary>
public sealed class DebugPageTests
{
    private const string John = "john@example.com";
    private const string Jane = "jane@example.com";
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_KeepTheSettingPerPerson()
    {
        await using var database = TestDatabase.Create((services, _) => services.AddSingleton<BoardTrafficSetting>());
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Users.AddRange(new User { Email = John, DisplayName = "John" }, new User { Email = Jane, DisplayName = "Jane" });
            await db.SaveChangesAsync(Cancel);
        }

        var setting = database.Services.GetRequiredService<BoardTrafficSetting>();
        List<string> changed = [];
        setting.Changed += (_, email) => changed.Add(email);

        await setting.SetAsync(John, true, Cancel);
        await setting.SetAsync(John, true, Cancel);

        Assert.True(await setting.IsOnAsync(John, Cancel));
        Assert.False(await setting.IsOnAsync(Jane, Cancel));
        Assert.False(await setting.IsOnAsync("nobody@example.com", Cancel));
        Assert.Equal([John], changed);

        await setting.SetAsync(John, false, Cancel);
        Assert.False(await setting.IsOnAsync(John, Cancel));
    }
}
