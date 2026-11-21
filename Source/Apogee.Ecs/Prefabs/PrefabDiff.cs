using System;
using System.Collections.Generic;
using Apogee.Content.Assets;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Apogee.Reflection;

namespace Apogee.Ecs;

// What an instance of a prefab changed, worked out when the scene is saved (docs/manual/content/prefabs.md, "How overrides are stored").
//
// The live entities are compared with what the prefab expands to on its own (PrefabExpander, no overrides): the same entities, found by the place the expansion gives them. Nothing is recorded
// while editing; a field that is set back to the prefab's value is no longer an override, and code and scripts that changed an entity are captured like the editor is. Everything is compared as the
// loader would make it, not as the files spell it: "1.0" and "1" are one number, a field an element leaves out is zero, an asset is the same with or without its UID, and an entity field is the
// entity it names, whatever address it was written with.
//
// This class only judges one entity at a time (its name and its components); which entities pair up, and how the results are laid out in the element, is the world's (World.Scene.cs).
internal sealed unsafe class PrefabDiff
{
    private readonly World _world;
    private readonly World.SavePlan _plan;
    private readonly Expansion _expansion;
    private readonly string _instance;
    private readonly SortedDictionary<string, ComponentInfo> _scripts;

    /// <param name="world">The world the live entities are in.</param>
    /// <param name="plan">What is being written: the address the document gives each entity it writes (entity fields are written as these).</param>
    /// <param name="expansion">What the instance's prefab expands to with no overrides, at <paramref name="instance"/>.</param>
    /// <param name="instance">The address of the instance's root.</param>
    /// <param name="scripts">Collects the script-defined components that are written whole (added to an entity the prefab did not have them on), for the document's <c>Components</c> element.</param>
    public PrefabDiff(World world, World.SavePlan plan, Expansion expansion, string instance, SortedDictionary<string, ComponentInfo> scripts)
    {
        _world = world;
        _plan = plan;
        _expansion = expansion;
        _instance = instance;
        _scripts = scripts;
    }

    /// <summary>The address of an entity of the expansion relative to the instance, as an <c>&lt;Override entity="…"&gt;</c> writes it; null when it has none that can be written (an entity with no usable id).</summary>
    public string? RelativeAddress(ExpandedEntity member) => RelativeAddress(_instance, member);

    /// <summary>See <see cref="RelativeAddress(ExpandedEntity)"/>, for the instance at <paramref name="instance"/>.</summary>
    public static string? RelativeAddress(string instance, ExpandedEntity member)
    {
        string prefix = instance + "/";
        return EntityAddress.IsValid(member.Address) && member.Address.StartsWith(prefix, StringComparison.Ordinal) ? member.Address[prefix.Length..] : null;
    }

    /// <summary>The name the live entity has when it is not the one the prefab gives it; null when it is the same (or the entity has no name, which cannot be written as a change).</summary>
    public string? NameChange(Entity live, ExpandedEntity expected) =>
        _world.GetName(live) is { } name && name != expected.Name ? name : null;

    /// <summary>
    /// The component elements that turn the expected entity into the live one, in a fixed order so that saving twice writes the same text: for each component the prefab has, <c>remove="true"</c> when
    /// the live entity lost it or an element with only the fields that differ; then, by name, each component the live entity has and the prefab does not, written whole with <c>append="true"</c>.
    /// Components the world does not describe and the ones scenes leave out are never compared.
    /// </summary>
    public List<XmlNode> ComponentPatches(Entity live, ExpandedEntity expected)
    {
        var liveByName = new Dictionary<string, (Entity Id, ComponentInfo Info)>(StringComparer.Ordinal);
        foreach ((Entity id, ComponentInfo info) in _world.ComponentsOf(live))
        {
            if (!_world.IsSceneSkipped(info.Name))
            {
                liveByName[info.Name] = (id, info);
            }
        }

        var patches = new List<XmlNode>();
        foreach (XmlNode component in expected.Components)
        {
            // A component this world cannot describe was skipped when the entity was made: not having it says nothing.
            if (_world.IsSceneSkipped(component.Name) || _world.InfoByName(component.Name) is null)
            {
                continue;
            }

            if (!liveByName.Remove(component.Name, out (Entity Id, ComponentInfo Info) have))
            {
                var removed = new XmlNode(component.Name);
                removed.SetAttribute("remove", "true");
                patches.Add(removed);
            }
            else if (Patch(live, have.Id, have.Info, component) is { } patch)
            {
                patches.Add(patch);
            }
        }

        foreach (string name in new SortedSet<string>(liveByName.Keys, StringComparer.Ordinal))
        {
            (Entity id, ComponentInfo info) = liveByName[name];
            if (info.ClrType is null)
            {
                _scripts[name] = info;
            }

            XmlNode whole = _world.WriteComponent(live, id, info, _plan);
            whole.SetAttribute("append", "true");
            patches.Add(whole);
        }

        return patches;
    }

    // The element with the fields of one component that differ, or null when none does. A field that is not set in the element keeps the prefab's, so every difference is written, a "none" as "".
    private XmlNode? Patch(Entity live, Entity id, ComponentInfo info, XmlNode expected)
    {
        if (info.IsTag)
        {
            return null;
        }

        byte* data = _world.GetPointer(live, id);
        XmlNode? patch = null;
        foreach (FieldDef field in info.Fields)
        {
            if (Differs(live, info, field, data + field.Offset, expected.Attribute(field.Name), out string text))
            {
                patch ??= new XmlNode(info.Name);
                patch.SetAttribute(field.Name, text);
            }
        }

        // A component the prefab has is defined by the prefab's file, so a patch of it does not carry its schema: the scene's would pin the layout the component had when the scene was saved.
        return patch;
    }

    // Whether the field's value at `at` is not what the element (`expected`, null: not written, so zero) would make; `text` is the live value as the document would write it. A field that is 0 because
    // what it named could not be made (an asset that did not load, an entity of a prefab that is missing) is what it named, as far as the document is concerned: not a difference from the prefab.
    private bool Differs(Entity live, ComponentInfo info, FieldDef field, byte* at, string? expected, out string text)
    {
        switch (field.Kind)
        {
            case FieldKind.Entity:
            {
                ulong target = *(ulong*)at;
                string? written = _plan.TryAddress(target, out string? address) ? address : target == 0 ? _world.DanglingText(live, info.Name, field.Name, _plan) : null;
                text = written ?? "";
                return ExpectedEntity(expected) != (written is null ? "" : Relative(written));
            }

            case FieldKind.AssetRef:
            {
                // A type this world has no name for cannot be written to a scene either: nothing to compare.
                if (field.AssetType is not { } type || !_world.HasAssetType(type))
                {
                    text = "";
                    return false;
                }

                uint handle = *(uint*)at;
                AssetRef? wanted = expected is { Length: > 0 } && AssetRef.TryParse(expected, out AssetRef parsed) && !parsed.IsEmpty ? parsed : null;
                if (handle == 0 && _world.UnresolvedAsset(live, info.Name, field.Name) is { } unresolved)
                {
                    // It did not load, which is not a decision to have no asset: it is what it was written as, the same as the prefab's or not.
                    text = unresolved;
                    return !(AssetRef.TryParse(unresolved, out AssetRef named) && SameAsset(wanted, named, _world.Assets));
                }

                AssetRef? liveRef = handle == 0 ? null : _world.QuietAssetRefOf(field, handle);
                text = liveRef?.ToString() ?? "";
                return !SameAsset(wanted, liveRef, _world.Assets);
            }

            default:
                text = FieldText.Format(field, at);
                return text != Canonical(field, expected);
        }
    }

    // The value text of a field as the loader would leave it: parsed and written again (so "1.0" is "1"), and zero for what is not written or cannot be read.
    private static string Canonical(FieldDef field, string? text)
    {
        byte* value = stackalloc byte[32]; // the widest field is a Double3, 24 bytes
        new Span<byte>(value, 32).Clear();
        if (text is not null)
        {
            try
            {
                FieldText.Parse(field, text, value);
            }
            catch (FormatException)
            {
                new Span<byte>(value, 32).Clear();
            }
        }

        return FieldText.Format(field, value);
    }

    // The entity an expected Entity field names, as a place in the instance: "" for none (nothing written, not an address, or an address that names nothing in the expansion, as the loader would leave it).
    private string ExpectedEntity(string? text) =>
        text is { Length: > 0 } && EntityAddress.IsValid(text) && _expansion.Index.TryGetValue(text, out ExpandedEntity? entity) && !entity.IsRemoved ? Relative(entity.Address) : "";

    // An address as a place relative to the instance, so that the instance may be numbered anywhere: "@" is the root, "=…" is outside the instance.
    private string Relative(string address)
    {
        if (address == _instance)
        {
            return "@";
        }

        string prefix = _instance + "/";
        return address.StartsWith(prefix, StringComparison.Ordinal) ? address[prefix.Length..] : "=" + address;
    }

    /// <summary>
    /// Whether two references name the same asset: both nothing; both with a UID and the same one; otherwise both resolving to one file (a path alone, or a UID, is looked up by <paramref name="assets"/>
    /// when there is a database). A reference with and without its UID, or after the file moved, is the same asset.
    /// </summary>
    internal static bool SameAsset(AssetRef? a, AssetRef? b, AssetDatabase? assets)
    {
        if (a is not { } left || b is not { } right)
        {
            return a is null && b is null;
        }

        if (!left.Uid.IsNone && !right.Uid.IsNone)
        {
            return left.Uid == right.Uid;
        }

        string? leftPath = PathOf(left, assets);
        return leftPath is not null && leftPath == PathOf(right, assets);
    }

    private static string? PathOf(AssetRef reference, AssetDatabase? assets) =>
        assets?.Resolve(reference) ?? (reference.HasPath ? reference.Path : null);
}
