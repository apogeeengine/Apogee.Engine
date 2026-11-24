using System.Collections.Generic;
using Weft.Content.Import;
using Weft.Content.Packs;

namespace Weft.Export;

/// <summary>What <see cref="GameExporter.Export"/> exports and how.</summary>
public sealed record ExportOptions
{
    /// <summary>The game folder: the one with <c>game.wgam</c>. It is only read, never written to.</summary>
    public required string GameFolder { get; init; }

    /// <summary>The folder the export goes into: it does not exist, is empty, or holds an earlier export (it has an <c>.weft-export</c> file), which is then cleared.</summary>
    public required string OutputFolder { get; init; }

    /// <summary>The platform to export for.</summary>
    public required ExportTarget Target { get; init; }

    /// <summary>The folder of the published runtime for <see cref="Target"/> (<c>--runtime</c>); null looks for one, see <see cref="RuntimeFolder.Resolve"/>.</summary>
    public string? RuntimeDirectory { get; init; }

    /// <summary>Pack the imported artifacts and cooked documents instead of the sources (the game is imported first, in a copy). Without it the pack holds the files as they are.</summary>
    public bool Cooked { get; init; }

    /// <summary>Windows only: keep the console subsystem, so that the game opens a console (to read its log).</summary>
    public bool Console { get; init; }

    /// <summary>The key that encrypts the packs and is written into the executable (<see cref="PackKeySlot"/>); null does neither.</summary>
    public PackKey? Key { get; init; }

    /// <summary>The importers that import the game's assets when <see cref="Cooked"/>; null: the content library's (models, images). The runtime also has the shader importer.</summary>
    public IReadOnlyList<IAssetImporter>? Importers { get; init; }
}

/// <summary>What <see cref="GameExporter.Export"/> made.</summary>
/// <param name="Executable">The path of the executable.</param>
/// <param name="GameRoot">The folder with the game's <c>game.wgam</c> and <c>data/</c>.</param>
/// <param name="Files">Every file of the export, relative to the output folder with <c>/</c>, in ordinal order.</param>
/// <param name="Warnings">What the person exporting should know: the packs are not encrypted, an icon is too small...</param>
public sealed record ExportResult(string Executable, string GameRoot, IReadOnlyList<string> Files, IReadOnlyList<string> Warnings);
