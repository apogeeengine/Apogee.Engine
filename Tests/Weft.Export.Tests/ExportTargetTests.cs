using Xunit;

namespace Weft.Export.Tests;

public class ExportTargetTests
{
    [Theory]
    [InlineData("linux-x64", "linux", "x64")]
    [InlineData("win-x64", "win", "x64")]
    [InlineData("osx-arm64", "osx", "arm64")]
    [InlineData("osx-x64", "osx", "x64")]
    [InlineData("WIN-X64", "win", "x64")]
    public void SupportedRidsParse(string rid, string os, string arch)
    {
        Assert.True(ExportTarget.TryParse(rid, out ExportTarget target, out string? problem), problem);
        Assert.Equal(new ExportTarget(os, arch), target);
        Assert.Equal(os + "-" + arch, target.Rid);
    }

    [Theory]
    [InlineData("win-arm64")]
    [InlineData("linux-arm64")]
    [InlineData("android-arm64")]
    [InlineData("")]
    public void OtherRidsAreRefusedWithTheList(string rid)
    {
        Assert.False(ExportTarget.TryParse(rid, out _, out string? problem));
        Assert.Contains($"'{rid}'", problem, System.StringComparison.Ordinal);
        Assert.Contains("linux-x64, win-x64, osx-arm64, osx-x64", problem, System.StringComparison.Ordinal);
    }
}
