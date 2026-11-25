using System;
using System.Collections.Generic;
using System.Globalization;
using Weft.Content.Import;
using Weft.Content.Xml;
using Weft.Core.Assets;

namespace Weft.Content.Assets;

/// <summary>
/// The UID in a <c>&lt;asset&gt;.wimp</c>: the <c>uid</c> attribute of its <c>&lt;Import&gt;</c> root (<c>&lt;Import uid="uid://4k2m9x7q1w3ze" srgb="false"/&gt;</c>). Anything that rewrites a
/// sidecar (a settings panel) has to keep it: <c>ImportSidecar.SetUid(newText, ImportSidecar.ReadUid(oldText))</c>. The import settings and the import key ignore it
/// (<see cref="Import.ImportSettings.KeyText"/>), so a UID never makes an asset import again.
/// </summary>
public static class ImportSidecar
{
    /// <summary>The name of the root element.</summary>
    public const string RootName = "Import";

    /// <summary>The version of the sidecar this engine writes (the <c>version</c> attribute of the root). A sidecar without one is version 1.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The name of the element that lists a sub-asset.</summary>
    public const string SubAssetName = "SubAsset";

    /// <summary>The suffix that makes the sidecar's name from the asset's.</summary>
    public static readonly string Suffix = AssetFormats.Sidecar.Extension;

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

    /// <summary>
    /// The sidecar's text after an import, as version 2: <c>version</c> and <c>sourceHash</c> set on the root (replaced in place or added after the last attribute) and a <c>&lt;SubAsset&gt;</c> element added for each of
    /// <paramref name="newSubAssets"/> (the ones this import minted; the ones already there stay as they are). Nothing else changes: comments, layout, other attributes, and the elements this engine does not use yet
    /// (<c>Clip</c>, <c>Lod</c>, <c>Collision</c>) stay byte for byte. The UID is <see cref="SetUid"/>'s.
    /// </summary>
    /// <exception cref="ContentException">The text is not an <c>&lt;Import&gt;</c> document the edit can be made in.</exception>
    public static string Refresh(string sidecarText, string sourceHash, IReadOnlyList<ImportSubAsset> newSubAssets)
    {
        string text = SetRootAttribute(sidecarText, "version", CurrentVersion.ToString(CultureInfo.InvariantCulture));
        text = SetRootAttribute(text, "sourceHash", sourceHash);
        foreach (ImportSubAsset sub in newSubAssets)
        {
            if (!RootChildren.TryScan(text, out RootChildren.Layout? layout))
            {
                throw new ContentException("The import settings cannot be read as text to add a sub-asset to them.");
            }

            text = RootChildren.Append(text, layout, $"<{SubAssetName} name=\"{EscapeAttribute(sub.Name)}\" uid=\"{sub.Uid}\"/>");
        }

        Check(text, sourceHash, newSubAssets);
        return text;
    }

    /// <summary>The text with a new UID for every <c>&lt;SubAsset&gt;</c>: a copy of an asset and its sidecar that is given a UID of its own must not share its sub-assets' UIDs with the original.</summary>
    /// <exception cref="ContentException">The text cannot be read as text to edit.</exception>
    public static string RenewSubAssetUids(string sidecarText)
    {
        if (!RootChildren.TryScan(sidecarText, out RootChildren.Layout? layout))
        {
            throw new ContentException("The import settings cannot be read as text to give their sub-assets new UIDs.");
        }

        string text = sidecarText;
        for (int i = layout.Children.Count - 1; i >= 0; i--)
        {
            RootChildren.Child child = layout.Children[i];
            if (child.Name == SubAssetName && child.Find("uid") is { } uid)
            {
                text = RootChildren.SetValue(text, uid, AssetUid.New().ToString());
            }
        }

        return text;
    }

    /// <summary>The text without its <c>&lt;SubAsset&gt;</c> elements (and the white space before each); unchanged when it has none or cannot be read as text.</summary>
    internal static string RemoveSubAssets(string sidecarText)
    {
        if (!RootChildren.TryScan(sidecarText, out RootChildren.Layout? layout))
        {
            return sidecarText;
        }

        string text = sidecarText;
        for (int i = layout.Children.Count - 1; i >= 0; i--)
        {
            if (layout.Children[i].Name == SubAssetName)
            {
                text = RootChildren.Remove(text, layout.Children[i]);
            }
        }

        return text;
    }

    private static string SetRootAttribute(string text, string name, string value)
    {
        if (!RootTag.TryFind(text, out RootTag.Tag? tag) || tag.Name != RootName)
        {
            throw new ContentException($"The import settings are not an <{RootName}> element: cannot add {name}.");
        }

        return RootTag.Set(text, tag, name, value);
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>Reads the edited text back: a surprise in the text is an error, not a damaged file.</summary>
    private static void Check(string text, string sourceHash, IReadOnlyList<ImportSubAsset> added)
    {
        try
        {
            XmlNode root = XmlNode.Parse(text.TrimStart('\uFEFF'), "the import settings");
            int found = 0;
            foreach (XmlNode child in root.Children)
            {
                foreach (ImportSubAsset sub in added)
                {
                    if (child.Name == SubAssetName && child.Attribute("name") == sub.Name && child.Attribute("uid") == sub.Uid.ToString())
                    {
                        found++;
                    }
                }
            }

            if (root.Attribute("version") != CurrentVersion.ToString(CultureInfo.InvariantCulture) || root.Attribute("sourceHash") != sourceHash || found != added.Count)
            {
                throw new ContentException("Updating the import settings did not work.");
            }
        }
        catch (XmlParseException e)
        {
            throw new ContentException($"Cannot update the import settings: {e.Message}", e);
        }
    }
}
