using Contactor.Web.Features.Languages;

namespace Contactor.Tests.Web;

/// <summary>A text made outside a page: put in words by a page's localizer, and in English for the logs.</summary>
public sealed class UiTextTests
{
    [Fact]
    public void Should_SayTheSameWords_OnAPageAndInTheLogs()
    {
        var text = UiText.Of("Setup.Tailscale.Approve", "relays");

        Assert.Equal("Approve relays in Tailscale's admin console, under Machines. Contactor goes on by itself.", text.English());
        Assert.Equal(text.English(), text.In(TestTexts.Localizer));
        Assert.Equal(text.English(), text.ToString());
    }

    [Fact]
    public void Should_KeepTheValueAsWritten_WithBracesInIt()
    {
        var text = UiText.Of("Setup.Tailscale.ServeFailed", "{0} {oops}");

        Assert.Equal("Tailscale Serve didn't start: {0} {oops}", text.English());
        Assert.Equal(text.English(), text.In(TestTexts.Localizer));
    }

    [Fact]
    public void Should_BeEqual_WithTheSameKeyAndValues()
    {
        Assert.Equal(UiText.Of("Setup.Tailscale.Approve", "relays"), UiText.Of("Setup.Tailscale.Approve", "relays"));
        Assert.NotEqual(UiText.Of("Setup.Tailscale.Approve", "relays"), UiText.Of("Setup.Tailscale.Approve", "other"));
        Assert.NotEqual(UiText.Of("Setup.Tailscale.Stopped"), UiText.Of("Setup.Tailscale.CheckFailed"));
    }

    [Fact]
    public void Should_SayTheEnglish_WhateverTheFormats()
    {
        using var swedish = TestCulture.Formats("sv-SE");

        Assert.Equal("Tailscale is stopped on this machine.", UiText.Of("Setup.Tailscale.Stopped").English());
    }
}
