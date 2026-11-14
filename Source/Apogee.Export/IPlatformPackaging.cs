using System;
using System.Collections.Generic;
using Apogee.Content.Packages;

namespace Apogee.Export;

/// <summary>What a platform's last step of an export needs to know and can change.</summary>
public sealed class PlatformPackagingContext
{
    /// <summary>What was asked for.</summary>
    public required ExportOptions Options { get; init; }

    /// <summary>The manifest of the game being exported.</summary>
    public required GameManifest Manifest { get; init; }

    /// <summary>Where the parts of the export are. The runtime, the packs and the key are in place.</summary>
    public required ExportLayout Layout { get; init; }

    /// <summary>The bytes of the file <see cref="GameManifest.Icon"/> names; null when the game has no icon, or the file is missing (a warning says so).</summary>
    public byte[]? IconFile { get; init; }

    /// <summary>The warnings of the export; a step adds to them.</summary>
    public required List<string> Warnings { get; init; }

    /// <summary>Tells the person exporting what is being done.</summary>
    public required Action<string> Log { get; init; }

    /// <summary>
    /// The <c>Icon</c> that the exported <c>game.xml</c> names, relative to <see cref="ExportLayout.GameRoot"/>: null (the default) when the icon is in the executable or the bundle, the path of a file the
    /// step wrote when the runtime draws the window's icon itself (Linux).
    /// </summary>
    public string? ExportedIcon { get; set; }
}

/// <summary>The last step of an export, which is different on every operating system: the icon, the version information, the bundle. It runs after the key is written into the executable.</summary>
public interface IPlatformPackaging
{
    /// <summary>Does the platform's step.</summary>
    /// <exception cref="ExportException">The step could not be done.</exception>
    void Apply(PlatformPackagingContext context);
}
