using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>Where the database is, for an installation whose compose.yml still maps the folder earlier images used.</summary>
public sealed class DatabaseLocationTests
{
    private const string ImageDefault = "Data Source=/data/contactor.db";

    [Fact]
    public void Should_UseData_ForANewInstallation()
    {
        var (connectionString, location) = PersistenceServiceCollectionExtensions.Resolve(ImageDefault, _ => false);

        Assert.Equal(ImageDefault, connectionString);
        Assert.Equal(new DatabaseLocation("/data/contactor.db", IsEarlierFolder: false), location);
    }

    [Fact]
    public void Should_UseTheEarlierFolder_WhileOnlyItHasADatabase()
    {
        var (connectionString, location) = PersistenceServiceCollectionExtensions.Resolve(
            ImageDefault, path => path == PersistenceServiceCollectionExtensions.EarlierPath);

        Assert.Equal("Data Source=/var/lib/contactor/contactor.db", connectionString);
        Assert.True(location.IsEarlierFolder);
    }

    [Fact]
    public void Should_KeepData_OnceItHasADatabase()
    {
        var (connectionString, location) = PersistenceServiceCollectionExtensions.Resolve(ImageDefault, _ => true);

        Assert.Equal(ImageDefault, connectionString);
        Assert.False(location.IsEarlierFolder);
    }

    [Theory]
    [InlineData("Data Source=contactor.db")]
    [InlineData("Data Source=/srv/contactor/contactor.db")]
    public void Should_LeaveAnyOtherDatabaseAlone(string configured)
    {
        var (connectionString, location) = PersistenceServiceCollectionExtensions.Resolve(configured, _ => true);

        Assert.Equal(configured, connectionString);
        Assert.False(location.IsEarlierFolder);
    }
}
