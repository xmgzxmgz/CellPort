using Xunit;
using CellPort.Core.Services;

namespace CellPort.Core.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V2.0.0", "2.0.0")]
    [InlineData("1.0.1", "1.0.1")]
    [InlineData("1.2.3-beta", "1.2.3")]
    public void ParseTag_StripsPrefixAndPrerelease(string tag, string expected)
    {
        var v = UpdateCheckService.ParseTag(tag);
        Assert.NotNull(v);
        Assert.Equal(new Version(expected), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void ParseTag_ReturnsNullOnGarbage(string? tag)
    {
        Assert.Null(UpdateCheckService.ParseTag(tag));
    }

    [Fact]
    public void ParseTag_ComparisonDetectsNewerVersion()
    {
        var current = new Version("1.0.1");
        Assert.True(UpdateCheckService.ParseTag("v1.0.2") > current);
        Assert.True(UpdateCheckService.ParseTag("v1.0.1") <= current);
    }
}
