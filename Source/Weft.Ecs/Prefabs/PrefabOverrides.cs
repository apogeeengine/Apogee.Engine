using System;
using System.Collections.Generic;
using Weft.Content.Xml;
using Weft.Reflection;

namespace Weft.Ecs;

/// <summary>What an instance of a prefab has changed, one kind of change at a time (see <see cref="PrefabOverride"/>).</summary>
public enum PrefabOverrideKind
{
    /// <summary>A field of a component the prefab has holds another value.</summary>
    Field,

    /// <summary>An entity of the prefab has another name.</summary>
    Name,

    /// <summary>An entity has a component the prefab does not give it.</summary>
    AddedComponent,

    /// <summary>An entity lacks a component the prefab gives it.</summary>
    RemovedComponent,

    /// <summary>An entity (with everything below it) was added to the instance: it is not in the prefab.</summary>
    AddedEntity,

    /// <summary>An entity of the prefab (with everything below it) is not in the instance.</summary>
    RemovedEntity,

    /// <summary>
    /// Something the instance's element says that no entity shows, because what it names is not in the prefab now (an override of an entity the prefab lost, kept aside to apply again if it comes back).
    /// It can be reverted (dropped), not applied.
    /// </summary>
    Unapplied,
}

/// <summary>Which prefab file <see cref="World.ApplyOverride(Entity, PrefabOverride, PrefabApplyTarget)"/> writes an override into.</summary>
public enum PrefabApplyTarget
{
    /// <summary>
    /// The prefab the instance names. An override of something inside a prefab that this one nests is written as an override in the nested instance's element of this prefab's file; one of something
    /// this prefab defines itself is written into its entities.
    /// </summary>
    InstancePrefab,

    /// <summary>The prefab that defines the entity: for something inside a nested prefab, that nested prefab's own file. For anything the instance's prefab defines itself it is the same as <see cref="InstancePrefab"/>.</summary>
    SourcePrefab,
}

/// <summary>
/// One thing an instance of a prefab has changed, as <see cref="World.PrefabOverrides(Entity)"/> lists it: what an inspector shows with a mark, and what a tool reverts or applies. It is a value as of the
/// moment it was listed: after the entities change, list again.
/// </summary>
/// <param name="Kind">What kind of change it is.</param>
/// <param name="Target">
/// Where, as an address in the instance (<c>3</c>: entity 3 of the instance's prefab, <c>2/3</c>: entity 3 of the prefab nested as 2); the empty text is the instance's root. For
/// <see cref="PrefabOverrideKind.AddedEntity"/> it is the address of the entity the new one is below; for <see cref="PrefabOverrideKind.RemovedEntity"/>, the entity that is not there.
/// </param>
/// <param name="Component">The component's name for the kinds that are about one; otherwise null.</param>
/// <param name="Field">The field's name for <see cref="PrefabOverrideKind.Field"/>; otherwise null.</param>
/// <param name="Entity">
/// The live entity the change is on: the one with the changed field, name or component, the added entity itself. Empty for <see cref="PrefabOverrideKind.RemovedEntity"/> (there is none) and
/// <see cref="PrefabOverrideKind.Unapplied"/>.
/// </param>
/// <param name="Element">
/// The change as the instance's element would write it: the component patch (with the changed attributes, <c>append="true"</c> or <c>remove="true"</c>), the <c>&lt;Override&gt;</c> element, the added
/// <c>&lt;Entity&gt;</c> with everything below it. Entity fields in it are addresses of a throwaway numbering, so it is for showing, not for saving.
/// </param>
public sealed record PrefabOverride(PrefabOverrideKind Kind, string Target, string? Component, string? Field, Entity Entity, XmlNode Element)
{
    /// <summary>For <see cref="PrefabOverrideKind.Unapplied"/>: the place of the element among the ones the instance keeps aside.</summary>
    public int Index { get; init; }

    /// <summary>
    /// Whether this change is about the given component (and field) of the given entity: a changed field of it, or the component added or removed (which changes all of its fields). Null as the
    /// <paramref name="field"/> asks about the component as a whole.
    /// </summary>
    public bool Concerns(Entity entity, string component, string? field = null)
    {
        if (Entity != entity || Component != component)
        {
            return false;
        }

        return Kind switch
        {
            PrefabOverrideKind.AddedComponent or PrefabOverrideKind.RemovedComponent => true,
            PrefabOverrideKind.Field => field is null || field == Field,
            _ => false,
        };
    }
}

/// <summary>A prefab file an apply writes (<see cref="World.PreviewApply"/>) or wrote (<see cref="PrefabUpdate.Written"/>).</summary>
/// <param name="Path">The file.</param>
/// <param name="Original">What the file held before, byte for byte: what a tool keeps to offer an undo (writing it back and reading the prefab again).</param>
/// <param name="Reformatted">
/// Whether the file is laid out in a way the engine does not write (comments, its own indentation or attribute order): the apply writes it in the engine's layout, so more than the change differs, and a tool
/// warns before it does that to a file a person wrote.
/// </param>
public sealed record PrefabFileWrite(string Path, ReadOnlyMemory<byte> Original, bool Reformatted);

/// <summary>An override found in the element of an instance as written, and where it is in that element (for taking it out again).</summary>
internal sealed record FoundOverride(PrefabOverride Override, XmlNode Owner, string Absolute);

// Reads what an instance element holds (as World.WriteInstance wrote it) as a list of overrides, and takes one out again. Pure XML.
internal static class OverrideReader
{
    private const string EntityElement = "Entity";
    private const string OverrideElement = "Override";

    /// <summary>The attributes of a component element that are not fields.</summary>
    public static bool IsReserved(string attribute) => attribute is "remove" or "append" or "patch";

    /// <summary>
    /// The overrides of the instance element at <paramref name="address"/>. The first <paramref name="keptCount"/> children are what was kept aside; <paramref name="entityAt"/> gives the live entity
    /// at an address of the document (empty when there is none).
    /// </summary>
    public static List<FoundOverride> Read(XmlNode instance, string address, int keptCount, Func<string, Entity> entityAt)
    {
        var found = new List<FoundOverride>();
        int position = 0;
        foreach (XmlNode child in instance.Children)
        {
            if (position < keptCount)
            {
                var kept = new PrefabOverride(PrefabOverrideKind.Unapplied, child.Name == OverrideElement ? child.Attribute("entity") ?? "" : "", child.Name is EntityElement or OverrideElement ? null : child.Name, null, default, child) { Index = position };
                found.Add(new FoundOverride(kept, instance, address));
            }
            else if (child.Name == OverrideElement)
            {
                ReadOverride(instance, child, address, entityAt, found);
            }
            else if (child.Name == EntityElement)
            {
                AddedEntity(instance, child, "", address, entityAt, found);
            }
            else
            {
                ReadComponent(instance, child, "", entityAt(address), address, found);
            }

            position++;
        }

        return found;
    }

    private static void ReadOverride(XmlNode instance, XmlNode element, string instanceAddress, Func<string, Entity> entityAt, List<FoundOverride> found)
    {
        string target = element.Attribute("entity") ?? "";
        string absolute = instanceAddress + "/" + target;
        Entity entity = entityAt(absolute);
        if (element.Attribute("remove") == "true")
        {
            found.Add(new FoundOverride(new PrefabOverride(PrefabOverrideKind.RemovedEntity, target, null, null, default, element), instance, absolute));
            return;
        }

        if (element.Attribute("name") is not null)
        {
            found.Add(new FoundOverride(new PrefabOverride(PrefabOverrideKind.Name, target, null, null, entity, element), element, absolute));
        }

        foreach (XmlNode child in element.Children)
        {
            if (child.Name == EntityElement)
            {
                AddedEntity(element, child, target, absolute, entityAt, found, parentAbsolute: absolute);
            }
            else
            {
                ReadComponent(element, child, target, entity, absolute, found);
            }
        }
    }

    private static void AddedEntity(XmlNode owner, XmlNode element, string target, string absolute, Func<string, Entity> entityAt, List<FoundOverride> found, string? parentAbsolute = null)
    {
        Entity added = element.Attribute("id") is { } id ? entityAt(id) : default;
        found.Add(new FoundOverride(new PrefabOverride(PrefabOverrideKind.AddedEntity, target, null, null, added, element), owner, parentAbsolute ?? absolute));
    }

    private static void ReadComponent(XmlNode owner, XmlNode element, string target, Entity entity, string absolute, List<FoundOverride> found)
    {
        if (element.Attribute("remove") == "true")
        {
            found.Add(new FoundOverride(new PrefabOverride(PrefabOverrideKind.RemovedComponent, target, element.Name, null, entity, element), owner, absolute));
        }
        else if (element.Attribute("append") == "true")
        {
            found.Add(new FoundOverride(new PrefabOverride(PrefabOverrideKind.AddedComponent, target, element.Name, null, entity, element), owner, absolute));
        }
        else
        {
            foreach (XmlProperty attribute in element.Attributes)
            {
                if (!IsReserved(attribute.Name))
                {
                    found.Add(new FoundOverride(new PrefabOverride(PrefabOverrideKind.Field, target, element.Name, attribute.Name, entity, element), owner, absolute));
                }
            }
        }
    }

    /// <summary>Whether a given override (listed before) is the one found now. An added entity is told by the entity, everything else by what it is about.</summary>
    public static bool Is(PrefabOverride wanted, string wantedAbsolute, FoundOverride found)
    {
        PrefabOverride other = found.Override;
        if (wanted.Kind != other.Kind || wanted.Component != other.Component || wanted.Field != other.Field)
        {
            return false;
        }

        return wanted.Kind switch
        {
            PrefabOverrideKind.AddedEntity => !wanted.Entity.IsNull && wanted.Entity == other.Entity,
            PrefabOverrideKind.Unapplied => wanted.Index == other.Index,
            _ => wantedAbsolute == found.Absolute,
        };
    }

    /// <summary>Takes the override out of the instance element it was found in.</summary>
    public static void Remove(XmlNode instance, FoundOverride found)
    {
        PrefabOverride o = found.Override;
        switch (o.Kind)
        {
            case PrefabOverrideKind.Field:
                o.Element.RemoveAttribute(o.Field!);
                if (o.Element.Attributes.Count == 0 && o.Element.Children.Count == 0)
                {
                    found.Owner.Children.Remove(o.Element);
                }

                break;
            case PrefabOverrideKind.Name:
                found.Owner.RemoveAttribute("name");
                break;
            case PrefabOverrideKind.Unapplied:
                break;
            default:
                found.Owner.Children.Remove(o.Element);
                break;
        }

        // An <Override> that has nothing left to say is not written.
        instance.Children.RemoveAll(static child => child.Name == OverrideElement && child.Children.Count == 0 && child.Attributes.Count <= 1);
    }
}
