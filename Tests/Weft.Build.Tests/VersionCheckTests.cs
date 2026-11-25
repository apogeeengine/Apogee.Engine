using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Weft.Build.Tests;

/// <summary>Engine.props and <c>./build.sh version check</c>: what is read, and what the check refuses.</summary>
public sealed class VersionCheckTests : IDisposable
{
    private const string Props = """
        <Project>
          <PropertyGroup>
            <WeftVersionMajor>0</WeftVersionMajor>
            <WeftVersionMinor>2</WeftVersionMinor>
            <WeftVersionPatch>0</WeftVersionPatch>
            <WeftVersionStatus>alpha</WeftVersionStatus>
            <WeftVersionCodename></WeftVersionCodename>
            <WeftCopyrightYear>2026</WeftCopyrightYear>
            <WeftApiVersion>1</WeftApiVersion>
          </PropertyGroup>
        </Project>
        """;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("weft-version-");

    public void Dispose() => _root.Delete(recursive: true);

    private static string RepositoryRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Weft.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("Weft.slnx not found above the test output.");
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A repository that is as it should be: the props, and the two build files that read them.</summary>
    private void Repository(string props = Props)
    {
        Write("Engine.props", props);
        Write("Directory.Build.props", "<Project><Import Project=\"$(MSBuildThisFileDirectory)Engine.props\" /><PropertyGroup><Version>$(WeftVersion)</Version></PropertyGroup></Project>");
        Write("Native/CMakeLists.txt", "file(READ \"${CMAKE_CURRENT_LIST_DIR}/../Engine.props\" TEXT)");
        Write("Source/Weft.Core/Weft.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"X\"><Version>1.0.0</Version></PackageReference></ItemGroup></Project>");
    }

    private static EngineProps Parse(string text)
    {
        Assert.True(EngineProps.TryParse(text, out EngineProps? props, out string? problem), problem);
        return props!;
    }

    private static string Problem(string text)
    {
        Assert.False(EngineProps.TryParse(text, out EngineProps? props, out string? problem));
        Assert.Null(props);
        return problem!;
    }

    [Fact]
    public void EnginePropsGivesTheSevenValuesAndTheText()
    {
        EngineProps props = Parse(Props);

        Assert.Equal((0, 2, 0, "alpha", "", 2026, 1), (props.Major, props.Minor, props.Patch, props.Status, props.Codename, props.CopyrightYear, props.ApiVersion));
        Assert.Equal("0.2.0", props.Core);
        Assert.Equal("0.2.0-alpha", props.Text);
    }

    [Theory]
    [InlineData("alpha", "0.2.0-alpha")]
    [InlineData("beta", "0.2.0-beta")]
    [InlineData("rc1", "0.2.0-rc1")]
    [InlineData("rc12", "0.2.0-rc12")]
    [InlineData("stable", "0.2.0")]
    public void StableHasNoSuffixAndEveryOtherStatusIsTheSuffix(string status, string text) =>
        Assert.Equal(text, Parse(Props.Replace("alpha", status, StringComparison.Ordinal)).Text);

    [Theory]
    [InlineData("Alpha")]
    [InlineData("rc")]
    [InlineData("release")]
    [InlineData("")]
    public void AnotherStatusIsRefused(string status) =>
        Assert.Contains("WeftVersionStatus", Problem(Props.Replace("alpha", status, StringComparison.Ordinal)), StringComparison.Ordinal);

    [Fact]
    public void ACodenameIsKeptAndOnlyPlainTextIsAllowed()
    {
        Assert.Equal("Warp 2", Parse(Props.Replace("<WeftVersionCodename></WeftVersionCodename>", "<WeftVersionCodename>Warp 2</WeftVersionCodename>", StringComparison.Ordinal)).Codename);
        Assert.Contains("WeftVersionCodename", Problem(Props.Replace("<WeftVersionCodename></WeftVersionCodename>", "<WeftVersionCodename>a\"b</WeftVersionCodename>", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingValueANumberThatIsNotOneAndAFileThatIsNotXmlAreRefused()
    {
        Assert.Contains("<WeftApiVersion>", Problem(Props.Replace("<WeftApiVersion>1</WeftApiVersion>", "", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("version parts", Problem(Props.Replace("<WeftVersionMinor>2<", "<WeftVersionMinor>two<", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("version parts", Problem(Props.Replace("<WeftVersionMinor>2<", "<WeftVersionMinor>70000<", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("WeftApiVersion", Problem(Props.Replace("<WeftApiVersion>1<", "<WeftApiVersion>0<", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("WeftCopyrightYear", Problem(Props.Replace("2026", "26", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("well-formed", Problem("<Project><PropertyGroup>"), StringComparison.Ordinal);
        Assert.Contains("<Project>", Problem("<Other/>"), StringComparison.Ordinal);
    }

    [Fact]
    public void ARepositoryThatIsAsItShouldBeHasNoProblems()
    {
        Repository();

        Assert.Empty(VersionCommand.Check(_root.FullName));
    }

    [Fact]
    public void ThisRepositoryHasNoProblems()
    {
        // The check the static checks workflow runs: Engine.props is valid and nothing else sets a version.
        Assert.Empty(VersionCommand.Check(RepositoryRoot()));
    }

    [Fact]
    public void WithoutEnginePropsOrWithAWrongOneThereIsAProblem()
    {
        Assert.Equal(["Engine.props is missing."], VersionCommand.Check(_root.FullName));

        Repository(Props.Replace("alpha", "gamma", StringComparison.Ordinal));
        string problem = Assert.Single(VersionCommand.Check(_root.FullName));
        Assert.StartsWith("Engine.props: WeftVersionStatus is 'gamma'", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v0.2.0-alpha", true)]
    [InlineData("refs/tags/v0.2.0-alpha", true)]
    [InlineData("v0.2.0", false)]
    [InlineData("0.2.0-alpha", false)]
    [InlineData("v0.3.0-alpha", false)]
    [InlineData("v0.2.0-alpha2", false)]
    public void OnATagTheTagIsVAndTheVersion(string tag, bool fine)
    {
        Repository();

        string[] problems = [.. VersionCommand.Check(_root.FullName, tag)];

        if (fine)
        {
            Assert.Empty(problems);
        }
        else
        {
            Assert.Contains("is not 'v0.2.0-alpha'", Assert.Single(problems), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AStableReleaseIsTaggedWithoutTheStatus()
    {
        Repository(Props.Replace("alpha", "stable", StringComparison.Ordinal));

        Assert.Empty(VersionCommand.Check(_root.FullName, "v0.2.0"));
        Assert.Single(VersionCommand.Check(_root.FullName, "v0.2.0-stable"));
    }

    [Fact]
    public void AVersionSetInAProjectOrAPropsFileIsFoundWithItsLine()
    {
        Repository();
        Write("Source/Weft.Audio/Weft.Audio.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <AssemblyVersion>1.0.0.0</AssemblyVersion>\n  </PropertyGroup>\n</Project>");
        Write("Source/Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <Version>9.9.9</Version>\n  </PropertyGroup>\n</Project>");
        Write("Tests/Shared.targets", "<Project><PropertyGroup><FileVersion>1.2.3.4</FileVersion><InformationalVersion>x</InformationalVersion></PropertyGroup></Project>");

        string[] problems = [.. VersionCommand.Check(_root.FullName).Order(StringComparer.Ordinal)];

        Assert.Equal(4, problems.Length);
        Assert.Contains(problems, p => p.StartsWith("Source/Weft.Audio/Weft.Audio.csproj:3: sets <AssemblyVersion>", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Source/Directory.Build.props:3: sets <Version>", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("<FileVersion>", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("<InformationalVersion>", StringComparison.Ordinal));
    }

    [Fact]
    public void AVersionInCodeIsFound()
    {
        Repository();
        Write("Source/Weft.Core/AssemblyInfo.cs", "using System.Reflection;\n[assembly: Assembly" + "Version(\"1.0.0.0\")]\n"); // written in two parts: this file is scanned too
        Write("Source/Weft.Ecs/Info.cs", "[assembly: System.Reflection.AssemblyInformational" + "Version(\"1.0\")]\n");
        Write("Source/Weft.Ecs/Fine.cs", "// the AssemblyVersion of the engine is in Engine.props\nclass Fine { }\n");

        string[] problems = [.. VersionCommand.Check(_root.FullName)];

        Assert.Equal(2, problems.Length);
        Assert.All(problems, p => Assert.Contains("assembly version in code", p, StringComparison.Ordinal));
    }

    [Fact]
    public void OutputFoldersAndVendoredFilesAreNotLookedAt()
    {
        Repository();
        Write("Bin/obj/Weft.Core/Weft.Core.csproj.nuget.g.props", "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>");
        Write("ThirdParty/x/x.props", "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>");
        Write(".claude/worktrees/a/Directory.Build.props", "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>");

        Assert.Empty(VersionCommand.Check(_root.FullName));
    }

    [Fact]
    public void TheBuildFilesMustReadEngineProps()
    {
        Repository();
        Write("Directory.Build.props", "<Project><PropertyGroup><Version>0.2.0</Version></PropertyGroup></Project>");
        Write("Native/CMakeLists.txt", "project(WeftNative VERSION 0.2.0)");

        string[] problems = [.. VersionCommand.Check(_root.FullName)];

        Assert.Equal(["Directory.Build.props does not read Engine.props.", "Native/CMakeLists.txt does not read Engine.props."], problems.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheCommandReturnsOneForAProblemAndForAWrongCommandLine()
    {
        Repository();

        Assert.Equal(0, VersionCommand.Run(_root.FullName, []));
        Assert.Equal(0, VersionCommand.Run(_root.FullName, ["check"]));
        Assert.Equal(0, VersionCommand.Run(_root.FullName, ["check", "--tag", "v0.2.0-alpha"]));
        Assert.Equal(1, VersionCommand.Run(_root.FullName, ["check", "--tag", "v1.0.0"]));
        Assert.Equal(1, VersionCommand.Run(_root.FullName, ["check", "--tag"]));
        Assert.Equal(1, VersionCommand.Run(_root.FullName, ["bump"]));
        Assert.Equal(1, VersionCommand.Run(Path.Combine(_root.FullName, "nowhere"), ["check"]));
    }

    [Fact]
    public void TheNativeBuildReadsEveryValueTheHeaderNeeds()
    {
        string cmake = File.ReadAllText(Path.Combine(RepositoryRoot(), "Native", "CMakeLists.txt"));
        string template = File.ReadAllText(Path.Combine(RepositoryRoot(), "Native", "weft_version.h.in"));

        foreach (string name in new[] { "WeftVersionMajor", "WeftVersionMinor", "WeftVersionPatch", "WeftVersionStatus", "WeftVersionCodename", "WeftApiVersion" })
        {
            Assert.Contains(name, cmake, StringComparison.Ordinal);
        }

        foreach (string macro in new[] { "WEFT_VERSION_MAJOR", "WEFT_VERSION_MINOR", "WEFT_VERSION_PATCH", "WEFT_VERSION_API", "WEFT_VERSION_STATUS", "WEFT_VERSION_CODENAME", "WEFT_VERSION_STRING" })
        {
            Assert.Contains("#define " + macro + " ", template, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDocsBuildPassesTheVersionToTheSite()
    {
        Assert.Equal("Weft Engine 0.2.0-alpha", DocsCommand.Footer("0.2.0-alpha"));
        Assert.Contains("!ENV [" + DocsCommand.FooterVariable, File.ReadAllText(Path.Combine(RepositoryRoot(), "mkdocs.yml")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRepositoryVersionIsWhatTheToolsRead()
    {
        EngineProps props = EngineProps.Load(RepositoryRoot());

        Assert.NotEmpty(props.Text);
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+(-(alpha|beta|rc[0-9]+))?$", props.Text);
    }
}
