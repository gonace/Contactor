using Contactor.Web;

namespace Contactor.Tests.Web;

public sealed class AppBuildTests
{
    [Theory]
    [InlineData("2026.11.4+1fdaf341d2fee8bf7e12ab4859c01de27f0054df", "2026.11.4", "1fdaf341d2fee8bf7e12ab4859c01de27f0054df", "1fdaf34")]
    [InlineData("2026.11.4.1+abc", "2026.11.4.1", "abc", "abc")]
    [InlineData("2026.11.4", "2026.11.4", null, null)]
    [InlineData("0.0.0-dev+abc", AppBuild.Development, "abc", "abc")]
    [InlineData("+abc", AppBuild.Development, "abc", "abc")]
    [InlineData("2026.11.4+", "2026.11.4", null, null)]
    [InlineData("", AppBuild.Development, null, null)]
    [InlineData(null, AppBuild.Development, null, null)]
    public void Should_ReadTheVersionAndCommit(string? informational, string version, string? commit, string? shortCommit)
    {
        var build = AppBuild.Of(informational);

        Assert.Equal((version, commit, shortCommit), (build.Version, build.Commit, build.ShortCommit));
        Assert.Equal(version != AppBuild.Development, build.IsRelease);
    }

    [Fact]
    public void Should_BeADevelopmentBuild_WhenNotPublishedAsARelease()
    {
        Assert.Equal(AppBuild.Development, AppBuild.Current.Version);
    }
}
