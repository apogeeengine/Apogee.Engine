using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;

namespace Apogee.Export;

/// <summary>A platform an export is made for: an operating system and a processor, named as the .NET runtime identifier (<c>linux-x64</c>).</summary>
/// <param name="Os">The operating system: <c>linux</c>, <c>win</c> or <c>osx</c>.</param>
/// <param name="Arch">The processor: <c>x64</c> or <c>arm64</c>.</param>
public readonly record struct ExportTarget(string Os, string Arch)
{
    /// <summary>The targets the engine has complete native libraries for.</summary>
    public static IReadOnlyList<ExportTarget> Supported { get; } =
    [
        new("linux", "x64"),
        new("win", "x64"),
        new("osx", "arm64"),
        new("osx", "x64"),
    ];

    /// <summary>The names of <see cref="Supported"/>, separated by commas.</summary>
    public static string SupportedList => string.Join(", ", Supported.Select(t => t.Rid));

    /// <summary>The target of the machine that runs this program.</summary>
    public static ExportTarget Host
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            return new ExportTarget(os, RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");
        }
    }

    /// <summary>The runtime identifier: <c>linux-x64</c>, <c>win-x64</c>, <c>osx-arm64</c>, <c>osx-x64</c>.</summary>
    public string Rid => Os + "-" + Arch;

    /// <summary>Reads a runtime identifier. When it is not one of <see cref="Supported"/>, <paramref name="problem"/> says so and lists them.</summary>
    public static bool TryParse(string rid, out ExportTarget target, [NotNullWhen(false)] out string? problem)
    {
        foreach (ExportTarget supported in Supported)
        {
            if (string.Equals(supported.Rid, rid, StringComparison.OrdinalIgnoreCase))
            {
                target = supported;
                problem = null;
                return true;
            }
        }

        target = default;
        problem = $"'{rid}' is not a target the engine can export for. The targets are {SupportedList}.";
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Rid;
}
