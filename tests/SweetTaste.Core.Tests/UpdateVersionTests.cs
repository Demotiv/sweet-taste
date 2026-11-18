using SweetTaste.Core.Updates;

namespace SweetTaste.Core.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("0.1.0-alpha.2", "0.1.0-alpha.10", -1)]
    [InlineData("0.1.0-alpha.2", "0.1.0-beta.1", -1)]
    [InlineData("0.1.0-alpha.2", "0.1.0", -1)]
    [InlineData("0.1.0", "0.1.0+build.5", 0)]
    [InlineData("1.2.3", "1.2.2", 1)]
    [InlineData("v1.0.0", "1.0.0", 0)]
    public void TryParse_ComparesVersions(string left, string right, int expected)
    {
        Assert.True(UpdateVersion.TryParse(left, out var a));
        Assert.True(UpdateVersion.TryParse(right, out var b));

        Assert.Equal(expected, Math.Sign(a.CompareTo(b)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("alpha")]
    [InlineData("1.0.0-")]
    public void TryParse_RejectsInvalidVersions(string value)
    {
        Assert.False(UpdateVersion.TryParse(value, out _));
    }
}
