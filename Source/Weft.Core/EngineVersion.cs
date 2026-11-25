using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Weft.Core;

// The constants (Major, Minor, Patch, Build, Api, Status, Codename, Text, Commit) are written into obj by the WeftEngineVersion target of Weft.Core.csproj, from Engine.props.
public static partial class EngineVersion
{
    /// <summary>
    /// The version with the commit, the way the informational version of an assembly has it: <c>0.2.0-alpha+abc1234</c>, or just <see cref="Text"/> when the engine was built outside a git checkout.
    /// This is what the logs and the crash reports say.
    /// </summary>
    public static string Full => Commit.Length == 0 ? Text : Text + "+" + Commit;

    /// <summary>The runtime identifier of the machine the program runs on, for the platforms the engine runs on: <c>linux-x64</c>, <c>win-x64</c>, <c>osx-arm64</c>, ...</summary>
    public static string Platform
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unknown";
            return os + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        }
    }

    /// <summary>
    /// What <c>--version</c> prints: <c>Weft 0.2.0-alpha (abc1234) linux-x64 NativeAOT</c>, with the commit in parentheses when there is one, then the platform and whether the program is JIT-compiled (CoreCLR)
    /// or ahead of time (NativeAOT). A release with a codename has it after the version: <c>Weft 1.0.0 "Name" (abc1234) ...</c>.
    /// </summary>
    /// <param name="program">What the line starts with: <c>Weft</c> for the runtime, <c>Weft Editor</c> for the editor.</param>
    public static string Describe(string program = "Weft")
    {
        string name = Codename.Length == 0 ? Text : string.Create(CultureInfo.InvariantCulture, $"{Text} \"{Codename}\"");
        string commit = Commit.Length == 0 ? "" : " (" + Commit + ")";
        return program + " " + name + commit + " " + Platform + " " + (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "CoreCLR" : "NativeAOT");
    }
}
