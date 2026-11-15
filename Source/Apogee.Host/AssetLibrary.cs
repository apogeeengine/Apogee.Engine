using Apogee.Content.Assets;
using Apogee.Core.Assets;
using Apogee.Reflection;

namespace Apogee.Host;

/// <summary>
/// Asset identity for scripts, as the global <c>assets</c>: an asset is named by its path (<c>res://models/car.glb</c>), which people read, and has a UID (<c>uid://4k2m9x7q1w3ze</c>), which stays the same when
/// the file is renamed or moved. A reference is the text <c>path|uid://…</c>; a path alone and a <c>uid://…</c> alone are also references. <c>models.load</c> and the other loaders take a path or a <c>uid://</c> directly.
/// </summary>
[ApogeeClass(Name = "AssetLibrary")]
public sealed partial class AssetLibrary : EngineObject
{
    private readonly AssetDatabase _assets;

    /// <param name="assets">Which file has which UID, over the game's files.</param>
    public AssetLibrary(AssetDatabase assets) => _assets = assets;

    /// <summary>
    /// The file a path, a <c>uid://</c> or a reference (<c>path|uid://…</c>) means, as a path: the path itself when that file exists, otherwise the file that has the UID, otherwise nil. A UID found
    /// this way for a path that no longer leads anywhere means the file was moved.
    /// </summary>
    /// <param name="text">A path, a <c>uid://</c> or a reference.</param>
    [Method]
    public string? Resolve(string text)
    {
        if (!AssetRef.TryParse(text, out AssetRef reference))
        {
            throw new ValueException($"'{text}' is not an asset reference: expected a path, a uid:// or path|uid://");
        }

        return reference.IsEmpty ? null : _assets.Resolve(reference);
    }

    /// <summary>The UID of the asset at <paramref name="path"/> as <c>uid://…</c>, or nil when the file does not exist or has none (an asset gets its UID when the project is imported).</summary>
    /// <param name="path">The path of the asset.</param>
    [Method]
    public string? UidOf(string path) => _assets.UidOf(path) is { IsNone: false } uid ? uid.ToString() : null;

    /// <summary>The reference to the asset at <paramref name="path"/>, <c>path|uid://…</c>, or just the path when it has no UID: what to store where a script keeps a model or a picture across runs.</summary>
    /// <param name="path">The path of the asset.</param>
    [Method]
    public string RefOf(string path) => _assets.RefOf(path).ToString();
}
