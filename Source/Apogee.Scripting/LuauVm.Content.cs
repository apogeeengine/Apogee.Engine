using System;
using System.Collections.Generic;
using System.Linq;
using XmlConvert = System.Xml.XmlConvert;
using XmlException = System.Xml.XmlException;
using Apogee.Content.Xml;
using Apogee.Interop;
using Apogee.Native.Luau;
using Apogee.Reflection;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

public sealed unsafe partial class LuauVm
{
    private DefDatabase? _defs;
    private Apogee.Content.Packages.PackageSet? _packages;
    private IReadOnlyList<PackageRunResult> _packageRuns = [];
    private readonly List<LuauFunction> _defsListeners = [];

    /// <summary>
    /// The definitions scripts read through the <c>defs</c> library. Null (the default): the library is there and finds nothing. Setting it again (a reload) makes scripts see the new
    /// definitions from their next call; tables they already hold stay as they were.
    /// </summary>
    public DefDatabase? Defs
    {
        get => _defs;
        set
        {
            ThrowIfDisposed();
            _defs = value;
            fixed (byte* name = "apogee.defs\0"u8)
            {
                Lua.Pushnil(_main);
                Lua.Rawsetfield(_main, Lua.Registryindex, name); // the converted tables of the old ones
            }
        }
    }

    /// <summary>Tells the <c>packages</c> library what the game loaded (<see cref="PackageLoader.Load"/> does this).</summary>
    internal void SetPackages(Apogee.Content.Packages.PackageSet packages, IReadOnlyList<PackageRunResult> runs)
    {
        _packages = packages;
        _packageRuns = runs;
    }

    /// <summary>Registers the <c>xml</c> and <c>defs</c> libraries. They come with their definitions.</summary>
    internal static void RegisterContentNatives()
    {
        RegisterFileNatives();

        ScriptNatives.RegisterLibrary("xml", "decode", &XmlDecodeBody, "(text: string) -> XmlElement",
            "Reads XML text into an element: its name, attrs (by name), order (the attribute names as written), children, text and line. Raises an error with the line when the text is not well-formed.");
        ScriptNatives.RegisterLibrary("xml", "encode", &XmlEncodeBody, "(element: XmlElement) -> string",
            "Writes an element as XML text, with two spaces of indent. Attributes come in the order of `order`, then the rest sorted by name.");

        ScriptNatives.RegisterLibrary("packages", "list", &PackagesListBody, "() -> { PackageInfo }",
            "Every package found, the base game's and the mods: the ones that load (in load order) and then the ones that do not, with why.");
        ScriptNatives.RegisterLibrary("packages", "get", &PackagesGetBody, "(id: string) -> PackageInfo?", "One package by id, or nil.");

        ScriptNatives.RegisterLibrary("defs", "get", &DefsGetBody, "(type: string, id: string) -> any?",
            "The definition of this type (the element's name) and id as a read-only table of its attributes and child elements, or nil. The same table every time.");
        ScriptNatives.RegisterLibrary("defs", "all", &DefsAllBody, "(type: string) -> { any }",
            "Every definition of a type, in load order, as a new list of the read-only tables `get` gives.");
        ScriptNatives.RegisterLibrary("defs", "on_change", &DefsOnChangeBody, "(callback: () -> ()) -> ()",
            "Calls the function when the definitions are reloaded (a file changed while the game runs); read them again with get and all. A module that is reloaded registers its listener again.");
        ScriptNatives.RegisterLibrary("defs", "types", &DefsTypesBody, "() -> { string }", "The types that have definitions, sorted.");
        ScriptNatives.RegisterLibrary("defs", "sources", &DefsSourcesBody, "(type: string, id: string) -> { DefSource }",
            "The files that made a definition: the one that defined it, then each one that patched it, in load order.");
        ScriptNatives.RegisterLibrary("defs", "problems", &DefsProblemsBody, "() -> { DefProblem }",
            "What is wrong in the definitions: a severity (\"error\" or \"warning\"), the file and line, a path inside the definition, and a message.");
    }

    private static string StringArgument(State* L, int index, string what) =>
        Stack.Is(L, index, LuaType.Tstring) ? Stack.ReadString(L, index) : throw new ValueException($"{what} must be a string, got {Stack.TypeName(L, index)}");

    private static void SetStringField(State* L, string field, string value)
    {
        Stack.PushString(L, value);
        SetField(L, field);
    }

    private static void SetNumberField(State* L, string field, double value)
    {
        Lua.Pushnumber(L, value);
        SetField(L, field);
    }

    /// <summary>Sets <c>table[field]</c> to the value on top of the stack, popping it; the table is just below.</summary>
    private static void SetField(State* L, string field)
    {
        fixed (byte* key = Utf8.ToZeroTerminated(field))
        {
            Lua.Rawsetfield(L, -2, key);
        }
    }

    // ---- xml ------------------------------------------------------------------------------------------------------------------------

    private static int XmlDecodeBody(State* L)
    {
        string text = StringArgument(L, 1, "the text");
        XmlNode node;
        try
        {
            node = XmlNode.Parse(text, "xml");
        }
        catch (XmlParseException e)
        {
            throw new ValueException(e.Message);
        }

        PushXmlElement(L, node);
        return 1;
    }

    private static void PushXmlElement(State* L, XmlNode node)
    {
        Stack.Reserve(L, 8);
        Lua.Createtable(L, 0, 6);
        SetStringField(L, "name", node.Name);
        SetStringField(L, "text", node.Text);
        SetNumberField(L, "line", node.Line);

        Lua.Createtable(L, 0, node.Attributes.Count);
        foreach (XmlProperty attribute in node.Attributes)
        {
            SetStringField(L, attribute.Name, attribute.Value);
        }

        SetField(L, "attrs");

        Lua.Createtable(L, node.Attributes.Count, 0);
        for (int i = 0; i < node.Attributes.Count; i++)
        {
            Stack.PushString(L, node.Attributes[i].Name);
            Lua.Rawseti(L, -2, i + 1);
        }

        SetField(L, "order");

        Lua.Createtable(L, node.Children.Count, 0);
        for (int i = 0; i < node.Children.Count; i++)
        {
            PushXmlElement(L, node.Children[i]);
            Lua.Rawseti(L, -2, i + 1);
        }

        SetField(L, "children");
    }

    private static int XmlEncodeBody(State* L)
    {
        XmlNode node = ReadXmlElement(L, 1, 0);
        Stack.PushString(L, node.ToXml());
        return 1;
    }

    private static XmlNode ReadXmlElement(State* L, int index, int depth)
    {
        if (!Stack.Is(L, index, LuaType.Ttable))
        {
            throw new ValueException($"an element is a table with a name, got {Stack.TypeName(L, index)}");
        }

        if (depth > XmlNode.MaxDepth)
        {
            throw new ValueException($"the elements are nested more than {XmlNode.MaxDepth} deep");
        }

        Stack.Reserve(L, 8);
        int table = Lua.Absindex(L, index);
        var node = new XmlNode(CheckedName(ReadStringField(L, table, "name") ?? throw new ValueException("an element needs a name"), "element"));
        node.Text = ReadStringField(L, table, "text") ?? "";

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (GetField(L, table, "attrs") == (int)LuaType.Ttable)
        {
            int attrs = Lua.Gettop(L);
            for (int iterator = 0; (iterator = Lua.Rawiter(L, attrs, iterator)) >= 0;)
            {
                string name = Stack.Is(L, -2, LuaType.Tstring) ? Stack.ReadString(L, -2) : throw new ValueException("attributes are named by strings");
                attributes[CheckedName(name, "attribute")] = AttributeText(L, -1, name);
                Lua.Settop(L, -3);
            }
        }

        Lua.Settop(L, table);

        var order = new List<string>();
        if (GetField(L, table, "order") == (int)LuaType.Ttable)
        {
            int count = Lua.Objlen(L, -1);
            for (int i = 1; i <= count; i++)
            {
                if (Stack.RawGetI(L, -1, i) == (int)LuaType.Tstring)
                {
                    order.Add(Stack.ReadString(L, -1));
                }

                Lua.Settop(L, -2);
            }
        }

        Lua.Settop(L, table);

        foreach (string name in order.Where(attributes.ContainsKey).Distinct(StringComparer.Ordinal).Concat(attributes.Keys.Except(order, StringComparer.Ordinal).Order(StringComparer.Ordinal)))
        {
            node.Attributes.Add(new XmlProperty(name, attributes[name]));
        }

        if (GetField(L, table, "children") == (int)LuaType.Ttable)
        {
            int children = Lua.Gettop(L);
            int count = Lua.Objlen(L, children);
            for (int i = 1; i <= count; i++)
            {
                _ = Stack.RawGetI(L, children, i);
                node.Children.Add(ReadXmlElement(L, -1, depth + 1));
                Lua.Settop(L, -2);
            }
        }

        Lua.Settop(L, table);
        return node;
    }

    private static string AttributeText(State* L, int index, string name) => (LuaType)Lua.Type(L, index) switch
    {
        LuaType.Tstring => Stack.ReadString(L, index),
        LuaType.Tnumber => Stack.FormatNumber(Lua.Tonumberx(L, index, null)),
        LuaType.Tboolean => Lua.Toboolean(L, index) != 0 ? "true" : "false",
        _ => throw new ValueException($"the attribute '{name}' must be text, a number or a boolean, got {Stack.TypeName(L, index)}"),
    };

    private static string CheckedName(string name, string what)
    {
        try
        {
            return XmlConvert.VerifyName(name);
        }
        catch (XmlException)
        {
            throw new ValueException($"'{name}' is not a valid {what} name");
        }
    }

    /// <summary>Pushes <c>table[field]</c> (without metamethods) and returns its type.</summary>
    private static int GetField(State* L, int table, string field)
    {
        fixed (byte* key = Utf8.ToZeroTerminated(field))
        {
            return Lua.Rawgetfield(L, table, key);
        }
    }

    private static string? ReadStringField(State* L, int table, string field)
    {
        string? value = GetField(L, table, field) == (int)LuaType.Tstring ? Stack.ReadString(L, -1) : null;
        Lua.Settop(L, -2);
        return value;
    }

    // ---- defs -----------------------------------------------------------------------------------------------------------------------

    private static int DefsOnChangeBody(State* L)
    {
        if (!Stack.Is(L, 1, LuaType.Tfunction))
        {
            throw new ValueException($"on_change expects a function, got {Stack.TypeName(L, 1)}");
        }

        LuauVm vm = VmOf(L);
        LuauFunction listener = vm.CreateFunction(L, 1);
        vm._defsListeners.Add(listener);
        ScriptScope.OnReload(() =>
        {
            vm._defsListeners.Remove(listener);
            listener.Release();
        });
        return 0;
    }

    /// <summary>Calls the functions scripts gave to <c>defs.on_change</c>. The host does this after it set <see cref="Defs"/> again; a listener that fails is logged and the others still run.</summary>
    public void NotifyDefsChanged()
    {
        foreach (LuauFunction listener in _defsListeners.ToArray())
        {
            try
            {
                listener.Call([], []);
            }
            catch (ScriptException e)
            {
                Apogee.Core.Logging.Log.Error("Scripting", $"A defs.on_change function failed: {e.Message}");
            }
        }
    }

    private static int PackagesListBody(State* L)
    {
        LuauVm vm = VmOf(L);
        IReadOnlyList<Apogee.Content.Packages.SolvedPackage> all = vm._packages is null
            ? []
            : [.. vm._packages.LoadOrder, .. vm._packages.Packages.Where(p => !p.Enabled)];
        Stack.Reserve(L, 6);
        Lua.Createtable(L, all.Count, 0);
        for (int i = 0; i < all.Count; i++)
        {
            vm.PushPackage(L, all[i]);
            Lua.Rawseti(L, -2, i + 1);
        }

        return 1;
    }

    private static int PackagesGetBody(State* L)
    {
        string id = StringArgument(L, 1, "the id");
        LuauVm vm = VmOf(L);
        if (vm._packages?.Find(id) is not { } package)
        {
            Lua.Pushnil(L);
            return 1;
        }

        vm.PushPackage(L, package);
        return 1;
    }

    private void PushPackage(State* L, Apogee.Content.Packages.SolvedPackage package)
    {
        PackageRunResult? run = _packageRuns.FirstOrDefault(r => r.Package == package);
        Stack.Reserve(L, 6);
        Lua.Createtable(L, 0, 10);
        SetStringField(L, "id", package.Id);
        SetStringField(L, "name", package.Manifest.Name);
        SetStringField(L, "version", package.Manifest.Version.ToString());
        SetStringField(L, "author", package.Manifest.Author);
        SetStringField(L, "description", package.Manifest.Description);
        SetStringField(L, "kind", package.Kind.ToString().ToLowerInvariant());
        Lua.Pushboolean(L, package.Enabled ? 1 : 0);
        SetField(L, "loads");
        if (package.Reason is { } reason)
        {
            SetStringField(L, "reason", reason);
        }

        Lua.Pushboolean(L, run is { Ran: true } ? 1 : 0);
        SetField(L, "ran");
        if (run?.Error is { } error)
        {
            SetStringField(L, "error", error);
        }
    }

    private static int DefsGetBody(State* L)
    {
        string type = StringArgument(L, 1, "the type");
        string id = StringArgument(L, 2, "the id");
        LuauVm vm = VmOf(L);
        if (vm._defs?.Get(type, id) is not { } def)
        {
            Lua.Pushnil(L);
            return 1;
        }

        vm.PushDef(L, def);
        return 1;
    }

    private static int DefsAllBody(State* L)
    {
        string type = StringArgument(L, 1, "the type");
        LuauVm vm = VmOf(L);
        IReadOnlyList<Def> defs = vm._defs?.OfType(type) ?? [];
        Stack.Reserve(L, 4);
        Lua.Createtable(L, defs.Count, 0);
        for (int i = 0; i < defs.Count; i++)
        {
            vm.PushDef(L, defs[i]);
            Lua.Rawseti(L, -2, i + 1);
        }

        return 1;
    }

    private static int DefsTypesBody(State* L)
    {
        IReadOnlyList<string> types = VmOf(L)._defs?.Types ?? [];
        Stack.Reserve(L, 4);
        Lua.Createtable(L, types.Count, 0);
        for (int i = 0; i < types.Count; i++)
        {
            Stack.PushString(L, types[i]);
            Lua.Rawseti(L, -2, i + 1);
        }

        return 1;
    }

    private static int DefsSourcesBody(State* L)
    {
        string type = StringArgument(L, 1, "the type");
        string id = StringArgument(L, 2, "the id");
        List<DefSource> sources = VmOf(L)._defs?.Get(type, id)?.Sources ?? [];
        Stack.Reserve(L, 6);
        Lua.Createtable(L, sources.Count, 0);
        for (int i = 0; i < sources.Count; i++)
        {
            Lua.Createtable(L, 0, 4);
            SetStringField(L, "package", sources[i].Package);
            SetStringField(L, "path", sources[i].Path);
            SetNumberField(L, "line", sources[i].Line);
            SetStringField(L, "role", sources[i].Role.ToString().ToLowerInvariant());
            Lua.Rawseti(L, -2, i + 1);
        }

        return 1;
    }

    private static int DefsProblemsBody(State* L)
    {
        IReadOnlyList<SchemaProblem> problems = VmOf(L)._defs?.Problems ?? [];
        Stack.Reserve(L, 6);
        Lua.Createtable(L, problems.Count, 0);
        for (int i = 0; i < problems.Count; i++)
        {
            Lua.Createtable(L, 0, 5);
            SetStringField(L, "severity", problems[i].Severity.ToString().ToLowerInvariant());
            SetStringField(L, "path", problems[i].Path);
            if (problems[i].File is { } file)
            {
                SetStringField(L, "file", file);
            }

            SetNumberField(L, "line", problems[i].Line);
            SetStringField(L, "message", problems[i].Message);
            Lua.Rawseti(L, -2, i + 1);
        }

        return 1;
    }

    /// <summary>Pushes the table of a definition: made once, read-only all the way down, and the same one for every script until the definitions are replaced.</summary>
    private void PushDef(State* L, Def def)
    {
        Stack.Reserve(L, 8);
        fixed (byte* name = "apogee.defs\0"u8)
        {
            if (Lua.Rawgetfield(L, Lua.Registryindex, name) != (int)LuaType.Ttable)
            {
                Lua.Settop(L, -2);
                Lua.Createtable(L, 0, 64);
                Lua.Pushvalue(L, -1);
                Lua.Rawsetfield(L, Lua.Registryindex, name);
            }
        }

        string key = def.Type + "/" + def.Id; // an element name has no slash, so this is unambiguous (and a C string cannot hold a NUL)
        fixed (byte* cacheKey = Utf8.ToZeroTerminated(key))
        {
            if (Lua.Rawgetfield(L, -1, cacheKey) == (int)LuaType.Ttable)
            {
                Lua.Replace(L, -2); // the cache is under the table
                return;
            }

            Lua.Settop(L, -2);
            PushDefValue(L, def.Value);
            Lua.Pushvalue(L, -1);
            Lua.Rawsetfield(L, -3, cacheKey);
            Lua.Replace(L, -2);
        }
    }

    private static void PushDefValue(State* L, DefValue value)
    {
        Stack.Reserve(L, 6);
        switch (value)
        {
            case DefText text:
                Stack.PushString(L, text.Value);
                break;
            case DefNumber number:
                Lua.Pushnumber(L, number.Value);
                break;
            case DefBool flag:
                Lua.Pushboolean(L, flag.Value ? 1 : 0);
                break;
            case DefVector vector:
                Lua.Pushvector(L, (float)vector.X, (float)vector.Y, (float)vector.Z);
                break;
            case DefList list:
                Lua.Createtable(L, list.Items.Count, 0);
                for (int i = 0; i < list.Items.Count; i++)
                {
                    PushDefValue(L, list.Items[i]);
                    Lua.Rawseti(L, -2, i + 1);
                }

                Lua.Setreadonly(L, -1, 1);
                break;
            case DefRecord record:
                Lua.Createtable(L, 0, record.Count);
                foreach ((string name, DefValue member) in record.Members)
                {
                    PushDefValue(L, member);
                    SetField(L, name);
                }

                Lua.Setreadonly(L, -1, 1);
                break;
            default:
                Lua.Pushnil(L);
                break;
        }
    }
}
