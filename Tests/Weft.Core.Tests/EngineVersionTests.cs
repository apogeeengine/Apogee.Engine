using System;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Weft.Core.Tests;

public sealed class EngineVersionTests
{
    [Fact]
    public void TheTextIsTheThreePartsAndTheStatusUnlessTheEngineIsStable()
    {
        string numbers = string.Create(CultureInfo.InvariantCulture, $"{EngineVersion.Major}.{EngineVersion.Minor}.{EngineVersion.Patch}");

        string expected = EngineVersion.Status == "stable" ? numbers : numbers + "-" + EngineVersion.Status;
        Assert.Equal(EngineVersion.Text, expected);
        Assert.True(EngineVersion.Major >= 0 && EngineVersion.Minor >= 0 && EngineVersion.Patch >= 0 && EngineVersion.Build >= 0);
    }

    [Fact]
    public void TheStatusIsAlphaBetaAReleaseCandidateOrStable() =>
        Assert.Matches("^(alpha|beta|rc[0-9]+|stable)$", EngineVersion.Status);

    [Fact]
    public void AStableVersionHasNoSuffixAndEveryOtherHasTheStatus()
    {
        Assert.Equal(EngineVersion.Status == "stable", !EngineVersion.Text.Contains('-', StringComparison.Ordinal));
        Assert.Equal(EngineVersion.Status != "stable", EngineVersion.Text.EndsWith("-" + EngineVersion.Status, StringComparison.Ordinal));
    }

    [Fact]
    public void TheApiLevelIsThatOfThePackages() => Assert.True(EngineVersion.Api >= 1);

    [Fact]
    public void TheFullVersionAddsTheCommitAfterAPlus()
    {
        Assert.Matches("^([0-9a-f]{7,40})?$", EngineVersion.Commit);
        Assert.Equal(EngineVersion.Commit.Length == 0 ? EngineVersion.Text : EngineVersion.Text + "+" + EngineVersion.Commit, EngineVersion.Full);
    }

    [Fact]
    public void TheAssembliesAreVersionedFromTheSameFile()
    {
        Assembly assembly = typeof(EngineVersion).Assembly;
        string numbers = string.Create(CultureInfo.InvariantCulture, $"{EngineVersion.Major}.{EngineVersion.Minor}.{EngineVersion.Patch}");

        Assert.Equal(numbers + ".0", assembly.GetName().Version!.ToString());
        Assert.Equal(numbers + "." + (EngineVersion.Build % 65536).ToString(CultureInfo.InvariantCulture), assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Equal(EngineVersion.Full, assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
    }

    [Fact]
    public void DescribeIsTheLineThatVersionPrints()
    {
        string line = EngineVersion.Describe();

        Assert.StartsWith("Weft " + EngineVersion.Text, line, StringComparison.Ordinal);
        Assert.Equal(EngineVersion.Commit.Length > 0, line.Contains("(" + EngineVersion.Commit + ")", StringComparison.Ordinal));
        Assert.Matches(new Regex(" (linux|win|osx)-(x64|arm64|x86|arm) (CoreCLR|NativeAOT)$"), line);
        Assert.StartsWith("Weft Editor " + EngineVersion.Text, EngineVersion.Describe("Weft Editor"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCrashReportsAndTheLogsNameTheFullVersion()
    {
        Assert.Equal(EngineVersion.Full, Weft.Core.Diagnostics.CrashReport.EngineVersion);
    }
}
