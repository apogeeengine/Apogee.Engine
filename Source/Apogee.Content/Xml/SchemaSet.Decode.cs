using System;
using System.Collections.Generic;
using System.Linq;

namespace Apogee.Content.Xml;

public enum ProblemSeverity
{
    /// <summary>Something odd that still loads: a definition that replaces another, a kind with no schema.</summary>
    Warning,

    /// <summary>The content does not match its schema.</summary>
    Error,
}

/// <summary>Something wrong in a file: <see cref="Path"/> says where in the element (<c>Crate.items[2].count</c>), <see cref="Line"/> where in the file.</summary>
public sealed record SchemaProblem(ProblemSeverity Severity, string Path, string? File, int Line, string Message)
{
    public override string ToString() => $"{File ?? "?"}:{Line}: {(Severity == ProblemSeverity.Warning ? "warning: " : "")}{Path}: {Message}";
}

/// <summary>What <see cref="SchemaSet.Decode"/> made of an element: its data (with the defaults filled in, whatever the problems), and the problems.</summary>
/// <param name="Declared">False when the schemas have no element of that name: the value is the generic decoding and there are no problems to report.</param>
public sealed record DecodeResult(DefValue Value, IReadOnlyList<SchemaProblem> Problems, bool Declared)
{
    public bool HasErrors => Problems.Any(p => p.Severity == ProblemSeverity.Error);
}

public sealed partial class SchemaSet
{
    private sealed class DecodeContext(string? origin)
    {
        public List<SchemaProblem> Problems { get; } = [];

        /// <summary>A problem at <paramref name="node"/>: in the file it was read from, or else the file the caller named.</summary>
        public void Report(string path, XmlNode node, string message) =>
            Problems.Add(new SchemaProblem(ProblemSeverity.Error, path, node.Origin ?? origin, node.Line, message));
    }

    // ---- Decoding -------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks <paramref name="node"/> against the schemas and turns it into data. An element with a complex type is a record: its attributes (numbers, booleans and text by their types,
    /// a list type as a list), the defaults of the attributes it leaves out, and its child elements by name; a child that may repeat is a list; an element that holds nothing but one
    /// repeatable element (<c>&lt;units&gt;&lt;unit/&gt;&lt;unit/&gt;&lt;/units&gt;</c>) is that list; an element with a simple type is its value. Two conventions are read from an
    /// element's <c>xs:appinfo</c>: <c>map</c> (a container of <c>&lt;entry key="…"&gt;</c> items is a record by the keys; an item that has nothing but a key and a <c>value</c> gives
    /// the value) and <c>defaults</c> (the element left out is decoded as an empty one, so that the defaults of its attributes apply; a list left out is empty). An open element
    /// (<c>xs:any</c>) gives each child the global element <c>&lt;its name&gt;.&lt;child name&gt;</c> when the schemas have one, and keeps the others as they are.
    /// </summary>
    /// <param name="node">The element.</param>
    /// <param name="origin">The file it was read from, for the problems.</param>
    /// <param name="asElement">The global element to check it as (its own name by default).</param>
    public DecodeResult Decode(XmlNode node, string? origin = null, string? asElement = null)
    {
        var context = new DecodeContext(origin);
        if (!_elements.TryGetValue(asElement ?? node.Name, out ParticleDecl? declaration))
        {
            return new DecodeResult(DecodeGeneric(node), [], Declared: false);
        }

        DefValue value = DecodeElement(node, declaration, node.Name, context);
        return new DecodeResult(value, context.Problems, Declared: true);
    }

    private DefValue DecodeElement(XmlNode node, ParticleDecl declaration, string path, DecodeContext context)
    {
        try
        {
            ParticleDecl resolved = declaration;
            if (declaration.Ref is { } reference)
            {
                if (!_elements.TryGetValue(reference, out ParticleDecl? global))
                {
                    context.Report(path, node, $"the schemas have no element '{reference}'.");
                    return DecodeGeneric(node);
                }

                resolved = global;
            }

            ComplexTypeDef? complex = resolved.InlineComplex;
            ResolvedSimple? simple = null;
            if (complex is null && resolved.InlineSimple is { } inline)
            {
                simple = ResolveSimple(inline);
            }
            else if (complex is null && resolved.TypeName is { } typeName)
            {
                complex = FindComplexType(typeName);
                if (complex is null && (simple = ResolveSimpleName(typeName)) is null)
                {
                    context.Report(path, node, $"the schemas have no type '{typeName}'.");
                    return DecodeGeneric(node);
                }
            }
            else if (complex is null)
            {
                return DecodeGeneric(node); // no type: anything goes
            }

            if (simple is not null)
            {
                return DecodeSimpleElement(node, resolved, simple, path, context);
            }

            bool map = declaration.Map || resolved.Map;
            return DecodeComplexElement(node, ShapeOf(complex!), map, path, context);
        }
        catch (SchemaException e)
        {
            context.Report(path, node, e.Message);
            return DecodeGeneric(node);
        }
    }

    private static DefValue DecodeSimpleElement(XmlNode node, ParticleDecl declaration, ResolvedSimple type, string path, DecodeContext context)
    {
        CheckUnknownAttributes(node, [], anyAttribute: false, path, context);
        if (node.Children.Count > 0)
        {
            context.Report(path, node, $"<{node.Name}> holds a value, not elements.");
        }

        string text = node.Text.Length == 0 && declaration.Default is { } fallback ? fallback : node.Text;
        if (!type.TryConvert(text, out DefValue value, out string? problem))
        {
            context.Report(path, node, problem!);
        }

        return value;
    }

    private DefValue DecodeComplexElement(XmlNode node, TypeShape shape, bool map, string path, DecodeContext context)
    {
        var record = new DefRecord();
        DecodeAttributes(node, shape, path, context, record);

        if (shape.SimpleContent)
        {
            if (node.Children.Count > 0)
            {
                context.Report(path, node, $"<{node.Name}> holds text, not elements.");
            }

            if (!shape.Text!.TryConvert(node.Text, out DefValue text, out string? problem))
            {
                context.Report(path, node, problem!);
            }

            if (shape.Attributes.Count == 0)
            {
                return text;
            }

            record.Set("value", text);
            return record;
        }

        var slots = shape.Slots.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var values = new Dictionary<string, List<DefValue>>(StringComparer.Ordinal);
        var openValues = new List<(string Name, DefValue Value)>();
        var entries = new List<(XmlNode Node, DefValue Value)>();
        foreach (XmlNode child in node.Children)
        {
            if (slots.TryGetValue(child.Name, out ElementSlot? slot))
            {
                if (!values.TryGetValue(slot.Name, out List<DefValue>? list))
                {
                    values[slot.Name] = list = [];
                }

                string childPath = $"{path}.{child.Name}{(slot.Repeats ? $"[{list.Count + 1}]" : "")}";
                DefValue decoded = DecodeElement(child, slot.Declaration, childPath, context);
                list.Add(decoded);
                entries.Add((child, decoded));
            }
            else if (shape.Open)
            {
                string childPath = $"{path}.{child.Name}";
                DefValue decoded = _elements.TryGetValue(node.Name + "." + child.Name, out ParticleDecl? known)
                    ? DecodeElement(child, known, childPath, context)
                    : DecodeGeneric(child);
                openValues.Add((child.Name, decoded));
                entries.Add((child, decoded));
            }
            else
            {
                string allowed = shape.Slots.Count == 0 ? "it holds no elements" : "allowed: " + string.Join(", ", shape.Slots.Select(s => s.Name));
                context.Report(path, child, $"<{child.Name}> is not allowed in <{node.Name}> ({allowed}).");
            }
        }

        foreach (ElementSlot slot in shape.Slots)
        {
            int count = values.TryGetValue(slot.Name, out List<DefValue>? found) ? found.Count : 0;
            bool absentIsFine = count == 0 && IsDefaults(slot.Declaration);
            if (count < slot.Min && !absentIsFine)
            {
                context.Report(path, node, slot.Min == 1 ? $"<{node.Name}> needs a <{slot.Name}>." : $"<{node.Name}> needs at least {slot.Min} <{slot.Name}> (it has {count}).");
            }

            if (slot.Max != -1 && count > slot.Max)
            {
                context.Report(path, node, slot.Max == 1 ? $"<{node.Name}> has {count} <{slot.Name}>, it may have one." : $"<{node.Name}> has {count} <{slot.Name}>, at most {slot.Max}.");
            }
        }

        if (map)
        {
            AddMapEntries(node, entries, path, context, record);
            return record;
        }

        // An element with nothing but one repeatable element stands for the list of them.
        if (shape.Attributes.Count == 0 && shape.Slots.Count == 1 && shape.Slots[0].Repeats && !shape.Open && !shape.AnyAttribute)
        {
            return new DefList(values.TryGetValue(shape.Slots[0].Name, out List<DefValue>? only) ? only : []);
        }

        foreach (ElementSlot slot in shape.Slots)
        {
            if (values.TryGetValue(slot.Name, out List<DefValue>? list) && list.Count > 0)
            {
                record.Set(slot.Name, slot.Repeats ? new DefList(list) : list[0]);
            }
            else if (IsDefaults(slot.Declaration))
            {
                record.Set(slot.Name, slot.Repeats ? new DefList([]) : DecodeElement(new XmlNode(slot.Name, node.Line), slot.Declaration, $"{path}.{slot.Name}", context));
            }
        }

        foreach (IGrouping<string, (string Name, DefValue Value)> group in openValues.GroupBy(o => o.Name))
        {
            record.Set(group.Key, group.Count() == 1 ? group.First().Value : new DefList([.. group.Select(g => g.Value)]));
        }

        return record;
    }

    private bool IsDefaults(ParticleDecl declaration) => declaration.Defaults || declaration.Ref is { } reference && _elements.TryGetValue(reference, out ParticleDecl? global) && global.Defaults;

    /// <summary>The map convention: each entry becomes a member named by its <c>key</c>.</summary>
    private static void AddMapEntries(XmlNode container, List<(XmlNode Node, DefValue Value)> entries, string path, DecodeContext context, DefRecord record)
    {
        foreach ((XmlNode node, DefValue decoded) in entries)
        {
            DefValue value = decoded;
            if (node.Attribute("key") is not { Length: > 0 } key)
            {
                context.Report($"{path}.{node.Name}", node, $"an entry of <{container.Name}> needs a key.");
                continue;
            }

            if (record.Has(key))
            {
                context.Report($"{path}.{node.Name}", node, $"the key '{key}' is used twice in <{container.Name}>.");
                continue;
            }

            if (value is DefRecord entry)
            {
                // Nothing but a key and a value: the value is the entry.
                if (entry.Count == 2 && entry.Has("key") && entry.Has("value"))
                {
                    value = entry["value"]!;
                }
                else
                {
                    entry.Remove("key");
                }
            }

            record.Set(key, value);
        }
    }

    private void DecodeAttributes(XmlNode node, TypeShape shape, string path, DecodeContext context, DefRecord record)
    {
        foreach (AttributeDecl attribute in shape.Attributes)
        {
            string? text = node.Attribute(attribute.Name);
            if (attribute.Prohibited)
            {
                if (text is not null)
                {
                    context.Report($"{path}.{attribute.Name}", node, $"the attribute '{attribute.Name}' is not allowed.");
                }

                continue;
            }

            if (text is null)
            {
                if (attribute.Default is null)
                {
                    if (attribute.Required)
                    {
                        context.Report(path, node, $"<{node.Name}> needs the attribute '{attribute.Name}'.");
                    }

                    continue;
                }

                text = attribute.Default;
            }

            ResolvedSimple type;
            try
            {
                type = attribute.Inline is { } inline ? ResolveSimple(inline)
                    : attribute.TypeName is { } typeName ? ResolveSimpleName(typeName) ?? throw new SchemaException(attribute.Origin, attribute.Line, $"the type '{typeName}' of the attribute '{attribute.Name}' is not defined.")
                    : ResolvedSimple.Of(BaseKind.String);
            }
            catch (SchemaException e)
            {
                context.Report($"{path}.{attribute.Name}", node, e.Message);
                record.Set(attribute.Name, new DefText(text));
                continue;
            }

            if (!type.TryConvert(text, out DefValue value, out string? problem))
            {
                context.Report($"{path}.{attribute.Name}", node, problem!);
            }

            record.Set(attribute.Name, value);
        }

        CheckUnknownAttributes(node, shape.Attributes, shape.AnyAttribute, path, context, record);
    }

    private static void CheckUnknownAttributes(XmlNode node, List<AttributeDecl> declared, bool anyAttribute, string path, DecodeContext context, DefRecord? record = null)
    {
        foreach (XmlProperty attribute in node.Attributes)
        {
            if (IsAllowedAnywhere(attribute.Name) || declared.Exists(d => d.Name == attribute.Name))
            {
                continue;
            }

            if (anyAttribute)
            {
                record?.Set(attribute.Name, new DefText(attribute.Value));
            }
            else
            {
                context.Report(path, node, declared.Count == 0
                    ? $"<{node.Name}> has no attributes, but has '{attribute.Name}'."
                    : $"'{attribute.Name}' is not an attribute of <{node.Name}> (it has: {string.Join(", ", declared.Select(d => d.Name))}).");
            }
        }
    }

    /// <summary>Attributes of the content system and of XML itself, which no schema has to declare.</summary>
    internal static bool IsAllowedAnywhere(string name) =>
        name is "patch" or "remove" or "append" or "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal) || name.StartsWith("xsi:", StringComparison.Ordinal);

    /// <summary>What an element is when no schema says: attributes as text, children by name (a repeated name is a list), and its text as <c>value</c>; a text-only element is just its text.</summary>
    private static DefValue DecodeGeneric(XmlNode node)
    {
        var record = new DefRecord();
        foreach (XmlProperty attribute in node.Attributes)
        {
            if (!IsAllowedAnywhere(attribute.Name))
            {
                record.Set(attribute.Name, new DefText(attribute.Value));
            }
        }

        var order = new List<string>();
        var children = new Dictionary<string, List<DefValue>>(StringComparer.Ordinal);
        foreach (XmlNode child in node.Children)
        {
            if (!children.TryGetValue(child.Name, out List<DefValue>? list))
            {
                children[child.Name] = list = [];
                order.Add(child.Name);
            }

            list.Add(DecodeGeneric(child));
        }

        foreach (string name in order)
        {
            record.Set(name, children[name].Count == 1 ? children[name][0] : new DefList(children[name]));
        }

        if (node.Text.Length > 0)
        {
            if (record.Count == 0)
            {
                return new DefText(node.Text);
            }

            record.Set("value", new DefText(node.Text));
        }

        return record;
    }

    // ---- Types ----------------------------------------------------------------------------------------------------------------------

    private static string Local(string name)
    {
        int colon = name.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? name : name[(colon + 1)..];
    }

    private ComplexTypeDef? FindComplexType(string name) => _complexTypes.GetValueOrDefault(Local(name));

    /// <summary>The simple type called <paramref name="name"/>: one the schemas define, or a built-in. Null when there is none.</summary>
    private ResolvedSimple? ResolveSimpleName(string name)
    {
        if (_simpleTypes.TryGetValue(Local(name), out SimpleTypeDef? definition))
        {
            return ResolveSimple(definition);
        }

        return ResolvedSimple.TryGetBuiltin(name, out ResolvedSimple builtin) ? builtin : null;
    }

    private ResolvedSimple ResolveSimple(SimpleTypeDef definition, int depth = 0)
    {
        if (definition.Resolved is { } known)
        {
            return known;
        }

        if (depth > 32)
        {
            throw new SchemaException(definition.Origin, definition.Line, "a type restricts itself.");
        }

        ResolvedSimple baseType = _simpleTypes.TryGetValue(Local(definition.BaseName!), out SimpleTypeDef? parent)
            ? ResolveSimple(parent, depth + 1)
            : ResolvedSimple.TryGetBuiltin(definition.BaseName!, out ResolvedSimple builtin)
                ? builtin
                : throw new SchemaException(definition.Origin, definition.Line, $"the base type '{definition.BaseName}' is not defined.");

        if (definition.IsList)
        {
            baseType = new ResolvedSimple { Base = baseType.Base, Item = baseType };
        }

        try
        {
            return definition.Resolved = ResolvedSimple.Restrict(baseType, definition);
        }
        catch (ArgumentException e)
        {
            throw new SchemaException(definition.Origin, definition.Line, $"a pattern is not a valid regular expression: {e.Message}");
        }
    }

    /// <summary>A complex type with what it extends and the groups it uses laid out flat.</summary>
    private TypeShape ShapeOf(ComplexTypeDef type, int depth = 0)
    {
        if (type.Shape is { } known)
        {
            return known;
        }

        if (depth > 32)
        {
            throw new SchemaException(type.Origin, type.Line, $"the type '{type.Name}' extends itself.");
        }

        var shape = new TypeShape { AnyAttribute = type.AnyAttribute };
        if (type.BaseName is { } baseName)
        {
            ComplexTypeDef baseType = FindComplexType(baseName) ?? throw new SchemaException(type.Origin, type.Line, $"the type '{baseName}' (the base of '{type.Name}') is not defined.");
            TypeShape baseShape = ShapeOf(baseType, depth + 1);
            shape.Attributes.AddRange(baseShape.Attributes);
            foreach (ElementSlot slot in baseShape.Slots)
            {
                shape.Slots.Add(new ElementSlot(slot.Name, slot.Declaration) { Min = slot.Min, Max = slot.Max });
            }

            shape.SimpleContent = baseShape.SimpleContent;
            shape.Text = baseShape.Text;
            shape.Open = baseShape.Open;
            shape.AnyAttribute |= baseShape.AnyAttribute;
        }

        if (type.SimpleContent)
        {
            shape.SimpleContent = true;
            shape.Text = ResolveSimpleName(type.TextTypeName!) ?? throw new SchemaException(type.Origin, type.Line, $"the text type '{type.TextTypeName}' is not defined.");
        }

        shape.Attributes.AddRange(type.Attributes);
        foreach (string group in type.AttributeGroupRefs)
        {
            AddAttributeGroup(shape, group, type, depth + 1);
        }

        foreach (ParticleDecl particle in type.Particles)
        {
            AddParticle(shape, particle, 1, 1, type, depth + 1);
        }

        return type.Shape = shape;
    }

    private void AddAttributeGroup(TypeShape shape, string name, ComplexTypeDef owner, int depth)
    {
        if (depth > 32)
        {
            throw new SchemaException(owner.Origin, owner.Line, $"the attributeGroup '{name}' uses itself.");
        }

        GroupDef group = _attributeGroups.GetValueOrDefault(Local(name)) ?? throw new SchemaException(owner.Origin, owner.Line, $"the attributeGroup '{name}' is not defined.");
        shape.Attributes.AddRange(group.Attributes);
        foreach (string inner in group.AttributeGroupRefs)
        {
            AddAttributeGroup(shape, inner, owner, depth + 1);
        }
    }

    private void AddParticle(TypeShape shape, ParticleDecl particle, int outerMin, int outerMax, ComplexTypeDef owner, int depth)
    {
        if (depth > 32)
        {
            throw new SchemaException(owner.Origin, owner.Line, $"a group of the type '{owner.Name}' uses itself.");
        }

        (int min, int max) = Combine((particle.Min, particle.Max), outerMin, outerMax, inChoice: false);
        if (particle.IsAny)
        {
            shape.Open = true;
            return;
        }

        if (particle.GroupRef is { } groupName)
        {
            GroupDef group = _groups.GetValueOrDefault(Local(groupName)) ?? throw new SchemaException(owner.Origin, owner.Line, $"the group '{groupName}' is not defined.");
            foreach (ParticleDecl inner in group.Particles)
            {
                AddParticle(shape, inner, min, max, owner, depth + 1);
            }

            return;
        }

        ElementSlot? existing = shape.Slots.Find(s => s.Name == particle.Name);
        if (existing is null)
        {
            shape.Slots.Add(new ElementSlot(particle.Name, particle) { Min = min, Max = max });
        }
        else
        {
            existing.Min += min;
            existing.Max = existing.Max == -1 || max == -1 ? -1 : existing.Max + max;
        }
    }
}
