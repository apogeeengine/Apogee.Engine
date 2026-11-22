using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Apogee.Content.Import;

namespace Apogee.Host.Editing;

/// <summary>One kind of asset an editor lists and picks: its type name (the <see cref="Apogee.Reflection.FieldDef.AssetType"/> of the fields that hold it), the extensions of its files and a label for a person.</summary>
/// <param name="Type">The asset type, as <c>[Property(Asset = "…")]</c> spells it (<c>model</c>, <c>texture</c>, <c>scene</c>).</param>
/// <param name="Extensions">The extensions of its files, each with the dot and in lower case.</param>
/// <param name="Label">What a person reads (<c>Model</c>).</param>
public sealed record AssetKind(string Type, IReadOnlyList<string> Extensions, string Label);

/// <summary>
/// Which files are which kind of asset: the table behind the editor's asset picker and the asset field drawer. A kind is a type name (<c>model</c>) with the extensions of its files; a file belongs to the
/// kind that has its extension (not case-sensitive). The engine registers <c>model</c> (what the model importer reads), <c>texture</c> (the image importer's) and <c>scene</c>; a package or an editor
/// extension that adds a kind of asset (prefabs, materials) registers it with <see cref="Register"/> before the editor lists assets. Safe to use from several threads.
/// </summary>
public static class AssetKinds
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, AssetKind> s_kinds = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> s_typeOfExtension = new(StringComparer.OrdinalIgnoreCase);
    private static int s_version;

    static AssetKinds()
    {
        Register("model", ExtensionsOfImporter("model", [".glb", ".gltf"]), "Model");
        Register("texture", ExtensionsOfImporter("image", [".png", ".jpg", ".jpeg"]), "Texture");
        Register("scene", [".ascene"], "Scene");
    }

    /// <summary>Changes whenever a kind is registered: what lists files by kind looks again when it differs from what it saw.</summary>
    public static int Version => Volatile.Read(ref s_version);

    /// <summary>
    /// Adds the kind <paramref name="assetType"/>, or replaces what an earlier call registered for the same type. An extension belongs to one kind: registering one that another type has is refused.
    /// </summary>
    /// <param name="assetType">The type name that fields of this kind use as their asset type.</param>
    /// <param name="extensions">The extensions of its files (with the dot, such as <c>.aprefab</c>).</param>
    /// <param name="label">A name for a person.</param>
    /// <exception cref="ArgumentException">An extension has no dot, or is empty.</exception>
    /// <exception cref="InvalidOperationException">An extension belongs to another kind already.</exception>
    public static void Register(string assetType, IReadOnlyList<string> extensions, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(assetType);
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentException.ThrowIfNullOrEmpty(label);
        string[] lower = [.. extensions.Select(e => e is { Length: > 1 } && e[0] == '.' ? e.ToLowerInvariant() : throw new ArgumentException($"'{e}' is not an extension with its dot.", nameof(extensions))).Distinct()];
        lock (s_lock)
        {
            foreach (string extension in lower)
            {
                if (s_typeOfExtension.TryGetValue(extension, out string? other) && other != assetType)
                {
                    throw new InvalidOperationException($"{extension} is a '{other}' file already: it cannot also be a '{assetType}'.");
                }
            }

            if (s_kinds.TryGetValue(assetType, out AssetKind? old))
            {
                foreach (string extension in old.Extensions)
                {
                    s_typeOfExtension.Remove(extension);
                }
            }

            s_kinds[assetType] = new AssetKind(assetType, lower, label);
            foreach (string extension in lower)
            {
                s_typeOfExtension[extension] = assetType;
            }

            s_version++;
        }
    }

    /// <summary>The kinds, in the order of their type names.</summary>
    public static IReadOnlyList<AssetKind> All
    {
        get
        {
            lock (s_lock)
            {
                return [.. s_kinds.Values.OrderBy(k => k.Type, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>The extensions of the files of <paramref name="assetType"/> (with the dot, lower case), or an empty list for a type nobody registered.</summary>
    public static IReadOnlyList<string> ExtensionsOf(string assetType)
    {
        lock (s_lock)
        {
            return s_kinds.TryGetValue(assetType, out AssetKind? kind) ? kind.Extensions : [];
        }
    }

    /// <summary>What a person reads for <paramref name="assetType"/> (<c>Model</c>), or the type name itself when it is not registered.</summary>
    public static string LabelOf(string assetType)
    {
        lock (s_lock)
        {
            return s_kinds.TryGetValue(assetType, out AssetKind? kind) ? kind.Label : assetType;
        }
    }

    /// <summary>The asset type of the file at <paramref name="path"/> (by its extension, not case-sensitive), or null when no kind has it.</summary>
    public static string? TypeOfPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOfAny(['/', '\\']);
        if (dot <= slash + 1)
        {
            return null; // no extension, or a name that only starts with a dot
        }

        lock (s_lock)
        {
            return s_typeOfExtension.GetValueOrDefault(path[dot..]);
        }
    }

    private static IReadOnlyList<string> ExtensionsOfImporter(string name, IReadOnlyList<string> fallback) =>
        AssetImporter.Defaults.FirstOrDefault(i => i.Name == name) is { } importer ? importer.Extensions : fallback;
}
