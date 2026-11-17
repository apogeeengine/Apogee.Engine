using System;
using System.Collections.Generic;
using System.Globalization;
using Apogee.Content.Xml;
using Apogee.Core.Logging;

namespace Apogee.Ecs;

// The structure of a prefab file (see docs/manual/content/prefabs.md and Scene/prefab.xsd): a <Prefab version uid nextId> with its optional <Components> and exactly one <Entity> whose ids are
// positive numbers, each used once in the file.
internal static class PrefabDocument
{
    public const string RootName = "Prefab";
    public const int Version = 1;

    /// <summary>Whether <paramref name="text"/> is an entity id of a prefab: a number from 1 to 2^31-1.</summary>
    public static bool TryParseId(string? text, out int id) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 1;

    /// <summary>The root <c>Entity</c> of a document that passed <see cref="Check"/>.</summary>
    public static XmlNode RootEntity(XmlNode document) => document.Children.Find(c => c.Name == "Entity")!;

    /// <summary>
    /// Throws <see cref="FormatException"/> when the document is not a prefab this engine can read: another root element, a newer version, no root entity or more than one, an entity without a
    /// usable id or with one that another entity has. A <c>nextId</c> that is missing or not above every id is not an error: it is worked out again, with a warning.
    /// </summary>
    public static void Check(XmlNode document, string origin)
    {
        if (document.Name != RootName)
        {
            throw new FormatException($"{origin} is not a prefab: its root is <{document.Name}>, not <{RootName}>");
        }

        if (document.Attribute("version") is { } versionText)
        {
            if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            {
                throw new FormatException($"the prefab {origin} has the version '{versionText}', which is not a number");
            }

            if (version > Version)
            {
                throw new FormatException($"the prefab {origin} is version {version}; this engine reads up to {Version}");
            }
        }

        int roots = 0;
        foreach (XmlNode child in document.Children)
        {
            if (child.Name == "Entity")
            {
                roots++;
            }
        }

        if (roots != 1)
        {
            throw new FormatException($"the prefab {origin} has {roots} root entities: it must have exactly one <Entity>");
        }

        var seen = new HashSet<int>();
        int max = 0;
        CheckIds(RootEntity(document), origin, seen, ref max);
        if (!int.TryParse(document.Attribute("nextId"), NumberStyles.None, CultureInfo.InvariantCulture, out int next) || next <= max)
        {
            Log.Warn("Prefab", $"{origin}: nextId is missing or not above every id (the highest is {max}): it is worked out again, so a deleted id may be reused");
        }
    }

    /// <summary>
    /// The <c>nextId</c> a prefab file declares, or one above its highest id when it declares none or too small a one. It reads any document leniently (it is used on the file about to be
    /// overwritten, which may be damaged): an entity with no usable id counts as none.
    /// </summary>
    public static long NextIdOf(XmlNode document)
    {
        int highest = 0;
        foreach (XmlNode child in document.Children)
        {
            if (child.Name == "Entity")
            {
                highest = Math.Max(highest, HighestId(child));
            }
        }

        return int.TryParse(document.Attribute("nextId"), NumberStyles.None, CultureInfo.InvariantCulture, out int next) && next > highest ? next : (long)highest + 1;
    }

    private static int HighestId(XmlNode entity)
    {
        int highest = TryParseId(entity.Attribute("id"), out int id) ? id : 0;
        foreach (XmlNode child in entity.Children)
        {
            if (child.Name == "Entity")
            {
                highest = Math.Max(highest, HighestId(child));
            }
        }

        return highest;
    }

    private static void CheckIds(XmlNode entity, string origin, HashSet<int> seen, ref int max)
    {
        if (!TryParseId(entity.Attribute("id"), out int id))
        {
            throw new FormatException($"{origin}:{entity.Line}: an entity of a prefab needs an id, a number from 1 up (it has '{entity.Attribute("id")}')");
        }

        if (!seen.Add(id))
        {
            throw new FormatException($"{origin}:{entity.Line}: the entity id {id} is used twice in the prefab");
        }

        max = Math.Max(max, id);
        foreach (XmlNode child in entity.Children)
        {
            if (child.Name == "Entity")
            {
                CheckIds(child, origin, seen, ref max);
            }
        }
    }
}
