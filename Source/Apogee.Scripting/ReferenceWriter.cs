using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apogee.Reflection;

namespace Apogee.Scripting;

/// <summary>
/// Writes the engine's Luau API as Markdown pages for the Manual (<c>--luau-docs</c>), from the same registry as <see cref="DefinitionWriter"/>: one page per class
/// (<c>classes/Name.md</c>), one per script library (<c>libraries/name.md</c>), one for all enums (<c>enums.md</c>) and <c>reference.md</c>, which lists everything with the host's globals.
/// The output depends only on the registry, in a fixed order, with <c>\n</c> line ends.
/// </summary>
public static class ReferenceWriter
{
    /// <summary>The pages of everything registered now, by path relative to the output folder (always with <c>/</c>).</summary>
    public static IReadOnlyDictionary<string, string> Write(DefinitionOptions? options = null)
    {
        LuauVm.EnsureEngineClasses();
        LuauVm.RegisterEcsNatives();
        LuauVm.RegisterContentNatives();
        return Write(TypeRegistry.Classes, TypeRegistry.Enums, TypeRegistry.Libraries, options);
    }

    public static IReadOnlyDictionary<string, string> Write(IReadOnlyList<ClassInfo> classes, IReadOnlyList<EnumInfo> enums, IReadOnlyList<LibraryInfo> libraries, DefinitionOptions? options = null)
    {
        options ??= new DefinitionOptions();
        var enumsByName = enums.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var scriptable = classes
            .Where(c => c.Scriptable && c.Name != "EngineObject" && !options.HiddenClasses.Contains(c.Name))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        var context = new DefinitionWriter.Context(enumsByName, new HashSet<string>(scriptable.Select(c => c.Name).Append("EngineObject"), StringComparer.Ordinal));

        var pages = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (ClassInfo info in scriptable)
        {
            pages[$"classes/{info.Name}.md"] = ClassPage(info, context);
        }

        // A library is a registered static class, or a table of native functions, or both under one name.
        var libraryNames = libraries.Select(l => l.Name)
            .Concat(ScriptNatives.Libraries().Select(l => l.Library))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (string name in libraryNames)
        {
            pages[$"libraries/{name}.md"] = LibraryPage(name, libraries.FirstOrDefault(l => l.Name == name), context);
        }

        pages["enums.md"] = EnumsPage(enums);
        pages["reference.md"] = IndexPage(scriptable, libraryNames, libraries, options);
        return pages;
    }

    /// <summary>Writes the pages into <paramref name="directory"/>, replacing the ones a previous run wrote there (and nothing else).</summary>
    public static void WriteTo(string directory, DefinitionOptions? options = null)
    {
        IReadOnlyDictionary<string, string> pages = Write(options);
        foreach (string folder in new[] { "classes", "libraries" })
        {
            string path = Path.Combine(directory, folder);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        var utf8 = new UTF8Encoding(false);
        foreach ((string relative, string text) in pages)
        {
            string file = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text, utf8);
        }
    }

    private static string ClassPage(ClassInfo info, DefinitionWriter.Context context)
    {
        var text = new StringBuilder();
        Line(text, $"# {info.Name}");
        text.Append('\n');
        string parent = info.BaseName is { } name && context.Classes.Contains(name) ? name : "EngineObject";
        Line(text, parent == "EngineObject" ? "Extends `EngineObject`." : $"Extends [{parent}]({parent}.md).");
        text.Append('\n');
        if (info.Doc is not null)
        {
            Line(text, info.Doc);
            text.Append('\n');
        }

        if (info.Creatable)
        {
            Line(text, $"Create one with `{info.Name}.new()`.");
            text.Append('\n');
        }

        if (info.ModsBlocked)
        {
            Line(text, "Objects of this class are refused to mods.");
            text.Append('\n');
        }

        PropertyDef[] properties = info.Properties.Where(p => DefinitionWriter.IsIdentifier(p.ScriptName)).ToArray();
        if (properties.Length > 0)
        {
            Line(text, "## Properties");
            text.Append('\n');
            foreach (PropertyDef property in properties)
            {
                string doc = DefinitionWriter.WithRange(property.Doc, property.Meta) is { } d ? " " + d : "";
                string readOnly = property.Setter is null ? " *Read-only.*" : "";
                Line(text, $"- `{property.ScriptName}: {DefinitionWriter.TypeName(property.Type, context)}`{doc}{readOnly}");
            }

            text.Append('\n');
        }

        SignalDef[] signals = info.Signals.Where(s => DefinitionWriter.IsIdentifier(s.ScriptName)).ToArray();
        if (signals.Length > 0)
        {
            Line(text, "## Signals");
            text.Append('\n');
            foreach (SignalDef signal in signals)
            {
                string arguments = signal.Parameters.Length == 0 ? "()" : string.Join(", ", signal.Parameters.Select(p => DefinitionWriter.TypeName(p.Type, context)));
                Line(text, $"- `{signal.ScriptName}: Signal<{arguments}>`{(signal.Doc is null ? "" : " " + signal.Doc)}");
            }

            text.Append('\n');
        }

        MethodDef[] methods = info.Methods.Where(m => !m.IsStatic && DefinitionWriter.IsIdentifier(m.ScriptName)).ToArray();
        ScriptNatives.NativeMethod[] natives = ScriptNatives.Of(info.Name).ToArray();
        if (methods.Length > 0 || natives.Length > 0)
        {
            Line(text, "## Methods");
            text.Append('\n');
            foreach (MethodDef method in methods)
            {
                MethodSection(text, method, $"function {method.ScriptName}(self{string.Concat(method.Parameters.Select(p => ", " + DefinitionWriter.ParameterText(p, context)))}): {DefinitionWriter.ResultText(method, context)}");
            }

            foreach (ScriptNatives.NativeMethod native in natives)
            {
                Line(text, $"### {native.Name}");
                text.Append('\n');
                Line(text, "```luau");
                Line(text, native.Declaration);
                Line(text, "```");
                text.Append('\n');
            }
        }

        MethodDef[] statics = info.Methods.Where(m => m.IsStatic && DefinitionWriter.IsIdentifier(m.ScriptName)).ToArray();
        if (statics.Length > 0)
        {
            Line(text, "## Functions");
            text.Append('\n');
            foreach (MethodDef method in statics)
            {
                MethodSection(text, method, $"{info.Name}.{method.ScriptName}: {DefinitionWriter.FunctionType(method, context)}");
            }
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }

    private static string LibraryPage(string name, LibraryInfo? library, DefinitionWriter.Context context)
    {
        var text = new StringBuilder();
        Line(text, $"# {name}");
        text.Append('\n');
        Line(text, $"A global table of functions: `{name}.<function>(…)`.");
        text.Append('\n');
        if (library?.Doc is not null)
        {
            Line(text, library.Doc);
            text.Append('\n');
        }

        foreach (MethodDef method in library?.Methods.Where(m => DefinitionWriter.IsIdentifier(m.ScriptName)) ?? [])
        {
            MethodSection(text, method, $"{name}.{method.ScriptName}: {DefinitionWriter.FunctionType(method, context)}");
        }

        foreach ((_, IReadOnlyList<ScriptNatives.NativeFunction> functions) in ScriptNatives.Libraries().Where(l => l.Library == name))
        {
            foreach (ScriptNatives.NativeFunction function in functions)
            {
                Line(text, $"### {function.Name}");
                text.Append('\n');
                Line(text, "```luau");
                Line(text, $"{name}.{function.Name}: {function.Type}");
                Line(text, "```");
                text.Append('\n');
                if (function.Doc is not null)
                {
                    Line(text, function.Doc);
                    text.Append('\n');
                }
            }
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }

    private static string EnumsPage(IReadOnlyList<EnumInfo> enums)
    {
        var text = new StringBuilder();
        Line(text, "# Enums");
        text.Append('\n');
        Line(text, "In scripts an enum is a string, such as `\"no_title_bar\"`; a flags enum is a list of them, such as `{ \"no_move\", \"no_resize\" }`.");
        text.Append('\n');
        foreach (EnumInfo info in enums.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            Line(text, $"## {info.Name}");
            text.Append('\n');
            if (info.IsFlags)
            {
                Line(text, "A list of flags.");
                text.Append('\n');
            }

            if (info.Doc is not null)
            {
                Line(text, info.Doc);
                text.Append('\n');
            }

            foreach (EnumMemberInfo member in info.Members)
            {
                Line(text, $"- `\"{member.Name}\"`");
            }

            text.Append('\n');
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }

    private static string IndexPage(List<ClassInfo> classes, List<string> libraryNames, IReadOnlyList<LibraryInfo> libraries, DefinitionOptions options)
    {
        var text = new StringBuilder();
        Line(text, "# Luau API reference");
        text.Append('\n');
        Line(text, "Generated by `Apogee.Runtime --luau-docs` from the engine's registry: do not edit. The same information, as type definitions for luau-lsp, is `apogee.d.luau` (`--luau-defs`).");
        text.Append('\n');

        if (options.Globals.Count > 0)
        {
            Line(text, "## Globals");
            text.Append('\n');
            Line(text, "What the host sets in the VMs it runs scripts in.");
            text.Append('\n');
            foreach (HostGlobal global in options.Globals)
            {
                string type = global.Type;
                string target = classes.Any(c => c.Name == type) ? $"[`{type}`](classes/{type}.md)" : $"`{type}`";
                Line(text, $"- `{global.Name}`: {target}{(global.Doc is null ? "" : ". " + global.Doc)}");
            }

            text.Append('\n');
        }

        Line(text, "## Classes");
        text.Append('\n');
        foreach (ClassInfo info in classes)
        {
            Line(text, $"- [{info.Name}](classes/{info.Name}.md){(info.Doc is null ? "" : ": " + FirstSentence(info.Doc))}");
        }

        text.Append('\n');
        Line(text, "## Libraries");
        text.Append('\n');
        foreach (string name in libraryNames)
        {
            string? doc = libraries.FirstOrDefault(l => l.Name == name)?.Doc;
            Line(text, $"- [{name}](libraries/{name}.md){(doc is null ? "" : ": " + FirstSentence(doc))}");
        }

        text.Append('\n');
        Line(text, "## Enums");
        text.Append('\n');
        Line(text, "See [Enums](enums.md).");
        return text.ToString();
    }

    private static void MethodSection(StringBuilder text, MethodDef method, string signature)
    {
        Line(text, $"### {method.ScriptName}");
        text.Append('\n');
        Line(text, "```luau");
        Line(text, signature);
        Line(text, "```");
        text.Append('\n');
        if (method.Doc is not null)
        {
            Line(text, method.Doc);
            text.Append('\n');
        }
    }

    private static string FirstSentence(string doc)
    {
        int end = doc.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? doc : doc[..(end + 1)];
    }

    /// <summary>Always <c>\n</c>: the pages are the same on every platform.</summary>
    private static void Line(StringBuilder text, string line) => text.Append(line).Append('\n');
}
