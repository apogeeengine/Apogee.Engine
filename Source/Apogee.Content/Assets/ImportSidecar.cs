using System;
using Apogee.Core.Assets;

namespace Apogee.Content.Assets;

/// <summary>
/// The UID in a <c>&lt;asset&gt;.import.xml</c>: the <c>uid</c> attribute of its <c>&lt;Import&gt;</c> root (<c>&lt;Import uid="uid://4k2m9x7q1w3ze" srgb="false"/&gt;</c>). Anything that rewrites a
/// sidecar (a settings panel) has to keep it: <c>ImportSidecar.SetUid(newText, ImportSidecar.ReadUid(oldText))</c>. The import settings and the import key ignore it
/// (<see cref="Import.ImportSettings.KeyText"/>), so a UID never makes an asset import again.
/// </summary>
public static class ImportSidecar
{
    /// <summary>The name of the root element.</summary>
    public const string RootName = "Import";

    /// <summary>The suffix that makes the sidecar's name from the asset's.</summary>
    public const string Suffix = ".import.xml";

    /// <summary>The UID in the sidecar's text, or <see cref="AssetUid.None"/> when there is none, the text is empty, is not an <c>&lt;Import&gt;</c> or the attribute is not a valid UID. It never throws.</summary>
    public static AssetUid ReadUid(string sidecarText) => RootTag.ReadUid(sidecarText, RootName);

    /// <summary>
    /// The sidecar's text with <paramref name="uid"/> as its UID: replaced in place when there is one, added to the root tag otherwise, and nothing else changed (comments, other attributes,
    /// quotes and line endings stay). From the empty text it makes <c>&lt;Import uid="…"/&gt;</c> and a new line.
    /// </summary>
    /// <exception cref="ContentException">The text is not an <c>&lt;Import&gt;</c> document the edit can be made in.</exception>
    public static string SetUid(string sidecarText, AssetUid uid)
    {
        if (uid.IsNone)
        {
            throw new ArgumentException("A sidecar cannot be given no UID.", nameof(uid));
        }

        if (sidecarText.Length == 0)
        {
            return $"<{RootName} uid=\"{uid}\"/>\n";
        }

        if (!RootTag.TryFind(sidecarText, out RootTag.Tag? tag) || tag.Name != RootName)
        {
            throw new ContentException($"The import settings are not an <{RootName}> element: cannot add a uid.");
        }

        return RootTag.SetUid(sidecarText, uid, "the import settings");
    }
}
