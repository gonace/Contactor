using Contactor.Web.Features.Identity;

namespace Contactor.Tests.Web;

public sealed class IdentityRulesTests
{
    [Theory]
    [InlineData("nils@example.com", "Nils")]
    [InlineData("åsa@example.com", "Åsa")]
    [InlineData("lars.berg@example.com", "Lars.berg")]
    [InlineData("t@example.com", "T")]
    public void Should_NameUserAfterTheirAddress(string email, string displayName)
    {
        Assert.Equal(displayName, IdentityRules.DisplayNameFor(email));
    }

    [Fact]
    public void Should_CutLongNames()
    {
        var name = IdentityRules.DisplayNameFor(new string('a', 100) + "@example.com");

        Assert.Equal(40, name.Length);
    }

    [Theory]
    [InlineData("nils@example.com", true)]
    [InlineData("nils", false)]
    [InlineData("@example.com", false)]
    [InlineData("nils@", false)]
    [InlineData("a@b@example.com", false)]
    [InlineData("ni ls@example.com", false)]
    [InlineData("", false)]
    public void Should_AcceptOnlyUsableAddresses(string email, bool valid)
    {
        Assert.Equal(valid, IdentityRules.IsValidEmail(email));
    }

    [Fact]
    public void Should_RejectAddressesTooLongToStore()
    {
        Assert.False(IdentityRules.IsValidEmail(new string('a', 250) + "@example.com"));
    }
}
