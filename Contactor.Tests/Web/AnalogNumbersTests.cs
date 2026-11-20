using Contactor.Web.Features.Inputs;

namespace Contactor.Tests.Web;

/// <summary>A number typed into an analog input's scale, read as the app saves it.</summary>
public sealed class AnalogNumbersTests
{
    [Theory]
    [InlineData("", true, null)]
    [InlineData("  ", true, null)]
    [InlineData("2.08", true, "2.08")]
    [InlineData("2,08", true, "2.08")]
    [InlineData("-20", true, "-20")]
    [InlineData("−20", true, "-20")]
    [InlineData(" 5 ", true, "5")]
    [InlineData("1,000.5", false, null)]
    [InlineData("1e3", false, null)]
    [InlineData("abc", false, null)]
    [InlineData("- 5", false, null)]
    [InlineData("123456789012345678901234567890123", false, null)]
    public void Should_ReadATypedNumber(string text, bool ok, string? expected)
    {
        Assert.Equal(ok, AnalogNumbers.TryParse(text, out var value));
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }
}
