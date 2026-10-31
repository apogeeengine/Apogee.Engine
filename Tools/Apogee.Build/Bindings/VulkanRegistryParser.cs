using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClangSharp.Interop;

namespace Apogee.Build.Bindings;

/// <summary>
/// Reads the Khronos registry (<c>vk.xml</c>) into an <see cref="ApiModel"/>: Vulkan 1.0 to 1.4 and every extension that is not
/// platform specific, provisional or video (those need headers that are not part of the core ones). Type names keep their <c>Vk</c> prefix.
/// </summary>
public sealed class VulkanRegistryParser
{
    private const string Cdecl = "unmanaged[Cdecl]";

    private static readonly Dictionary<string, string> s_primitives = new()
    {
        ["void"] = "void", ["char"] = "byte", ["float"] = "float", ["double"] = "double", ["int"] = "int",
        ["uint8_t"] = "byte", ["int8_t"] = "sbyte", ["uint16_t"] = "ushort", ["int16_t"] = "short",
        ["uint32_t"] = "uint", ["int32_t"] = "int", ["uint64_t"] = "ulong", ["int64_t"] = "long", ["size_t"] = "nuint",
    };

    private static readonly HashSet<string> s_globalCommands =
        ["vkCreateInstance", "vkEnumerateInstanceVersion", "vkEnumerateInstanceExtensionProperties", "vkEnumerateInstanceLayerProperties"];

    private readonly BindingConfig _config;
    private readonly string _root;
    private readonly ApiModel _model = new();
    private readonly Dictionary<string, XElement> _types = [];
    private readonly Dictionary<string, string> _aliases = [];
    private readonly Dictionary<string, string> _basetypes = [];
    private readonly Dictionary<string, string> _bitmasks = [];
    private readonly Dictionary<string, bool> _handles = [];
    private readonly Dictionary<string, EnumInfo> _enums = [];
    private readonly Dictionary<string, string> _funcPointers = [];
    private readonly Dictionary<string, XElement> _commands = [];
    private readonly HashSet<string> _requiredTypes = [];
    private readonly List<string> _requiredCommands = [];
    private readonly HashSet<string> _failed = [];
    private readonly HashSet<string> _tags = [];
    private readonly Dictionary<string, BigInteger> _constants = [];

    /// <summary><c>VK_STRUCTURE_TYPE_APPLICATION_INFO</c> → the C# member name in <c>VkStructureType</c>.</summary>
    private readonly Dictionary<string, string> _structureTypes = [];

    public VulkanRegistryParser(BindingConfig config, string repositoryRoot)
    {
        _config = config;
        _root = repositoryRoot;
    }

    private sealed class EnumInfo
    {
        public bool IsBitmask { get; init; }

        public int BitWidth { get; init; } = 32;

        public List<(string Name, BigInteger Value)> Members { get; } = [];
    }

    public ApiModel Parse()
    {
        XElement registry = XDocument.Load(Path.Combine(_root, _config.Registry)).Root!;

        foreach (XElement tag in registry.Element("tags")!.Elements("tag"))
        {
            _tags.Add((string)tag.Attribute("name")!);
        }

        ReadConstants(registry);
        ReadEnums(registry);
        ReadTypes(registry);
        ReadCommands(registry);
        ReadRequirements(registry);
        EmitEnums();
        EmitHandles();
        EmitStructs();
        EmitCommands();
        return _model;
    }

    // ---- Reading -----------------------------------------------------------------------------------------------

    private static bool SupportsVulkan(string? api) => api is null || api.Split(',').Contains("vulkan");

    private static string? NameOf(XElement element) =>
        (string?)element.Attribute("name") ?? element.Element("name")?.Value ?? element.Element("proto")?.Element("name")?.Value;

    private void ReadConstants(XElement registry)
    {
        XElement constants = registry.Elements("enums").First(e => (string?)e.Attribute("name") == "API Constants");
        foreach (XElement constant in constants.Elements("enum"))
        {
            string name = (string)constant.Attribute("name")!;
            if ((string?)constant.Attribute("alias") is not null)
            {
                continue;
            }

            string value = ((string)constant.Attribute("value")!).Trim('(', ')');
            string type = (string?)constant.Attribute("type") ?? "";
            string csName = Naming.Pascal(name["VK_".Length..]);
            if (type == "float")
            {
                _model.Constants.Add(new ConstantModel(csName, "float", value.TrimEnd('F', 'f').ToLowerInvariant() + "f"));
            }
            else if (value.StartsWith('~'))
            {
                // (~0U), (~1U), (~0ULL): the complement of a small number at the type's width.
                bool wide = value.EndsWith("ULL", StringComparison.Ordinal);
                BigInteger operand = BigInteger.Parse(value[1..].TrimEnd('U', 'L'), CultureInfo.InvariantCulture);
                BigInteger max = wide ? ulong.MaxValue : uint.MaxValue;
                BigInteger result = max - operand;
                _constants[name] = result;
                _model.Constants.Add(new ConstantModel(csName, wide ? "ulong" : "uint", result.ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                BigInteger number = BigInteger.Parse(value, CultureInfo.InvariantCulture);
                _constants[name] = number;
                _model.Constants.Add(new ConstantModel(csName, type == "uint64_t" ? "ulong" : type == "uint32_t" ? "uint" : "int", value));
            }
        }
    }

    private void ReadEnums(XElement registry)
    {
        foreach (XElement group in registry.Elements("enums").Where(e => (string?)e.Attribute("name") != "API Constants"))
        {
            string name = (string)group.Attribute("name")!;
            var info = new EnumInfo
            {
                IsBitmask = (string?)group.Attribute("type") == "bitmask",
                BitWidth = (int?)group.Attribute("bitwidth") ?? 32,
            };
            foreach (XElement member in group.Elements("enum").Where(e => (string?)e.Attribute("alias") is null && SupportsVulkan((string?)e.Attribute("api"))))
            {
                info.Members.Add(((string)member.Attribute("name")!, MemberValue(member, 0)));
            }

            _enums[name] = info;
        }
    }

    private static BigInteger MemberValue(XElement member, int extensionNumber)
    {
        if ((string?)member.Attribute("value") is { } value)
        {
            return value.StartsWith("0x", StringComparison.Ordinal)
                ? BigInteger.Parse("0" + value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : BigInteger.Parse(value, CultureInfo.InvariantCulture);
        }

        if ((string?)member.Attribute("bitpos") is { } bit)
        {
            return BigInteger.One << int.Parse(bit, CultureInfo.InvariantCulture);
        }

        // Extension values: 1000000000 + (extension number - 1) * 1000 + offset.
        int number = (int?)member.Attribute("extnumber") ?? extensionNumber;
        int offset = int.Parse((string)member.Attribute("offset")!, CultureInfo.InvariantCulture);
        BigInteger result = 1_000_000_000 + ((number - 1) * 1000) + offset;
        return (string?)member.Attribute("dir") == "-" ? -result : result;
    }

    private void ReadTypes(XElement registry)
    {
        foreach (XElement type in registry.Element("types")!.Elements("type").Where(t => SupportsVulkan((string?)t.Attribute("api"))))
        {
            string? name = NameOf(type);
            if (name is null)
            {
                continue;
            }

            string? alias = (string?)type.Attribute("alias");
            if (alias is not null)
            {
                _aliases[name] = alias;
                continue;
            }

            _types[name] = type;
            switch ((string?)type.Attribute("category"))
            {
                case "basetype":
                    if (type.Element("type")?.Value is { } underlying && !type.Value.Contains('*', StringComparison.Ordinal))
                    {
                        _basetypes[name] = s_primitives.GetValueOrDefault(underlying, underlying);
                    }
                    else if (type.Value.Contains('*', StringComparison.Ordinal))
                    {
                        _basetypes[name] = "nint";
                    }

                    break;
                case "bitmask":
                    string flagBits = (string?)type.Attribute("requires") ?? (string?)type.Attribute("bitvalues") ?? "";
                    _bitmasks[name] = flagBits;
                    if (type.Element("type")?.Value == "VkFlags64")
                    {
                        _basetypes[name] = "ulong";
                    }

                    break;
                case "handle":
                    _handles[name] = type.Element("type")?.Value == "VK_DEFINE_HANDLE";
                    break;
                case "funcpointer":
                    _funcPointers[name] = "";
                    break;
            }
        }
    }

    private void ReadCommands(XElement registry)
    {
        foreach (XElement command in registry.Element("commands")!.Elements("command").Where(c => SupportsVulkan((string?)c.Attribute("api"))))
        {
            if ((string?)command.Attribute("alias") is { } alias)
            {
                _aliases[(string)command.Attribute("name")!] = alias;
            }
            else
            {
                _commands[command.Element("proto")!.Element("name")!.Value] = command;
            }
        }
    }

    private void ReadRequirements(XElement registry)
    {
        var blocks = new List<(XElement Require, int Number)>();
        foreach (XElement feature in registry.Elements("feature").Where(f => SupportsVulkan((string?)f.Attribute("api"))))
        {
            blocks.AddRange(feature.Elements("require").Select(r => (r, 0)));
        }

        foreach (XElement extension in registry.Element("extensions")!.Elements("extension"))
        {
            string supported = (string?)extension.Attribute("supported") ?? "";
            if (!supported.Split(',').Contains("vulkan")
                || (string?)extension.Attribute("platform") is not null
                || (string?)extension.Attribute("provisional") == "true"
                || ((string)extension.Attribute("name")!).Contains("_video_", StringComparison.Ordinal)
                || ((string)extension.Attribute("name")!).EndsWith("_video", StringComparison.Ordinal))
            {
                continue;
            }

            int number = int.Parse((string)extension.Attribute("number")!, CultureInfo.InvariantCulture);
            blocks.AddRange(extension.Elements("require").Select(r => (r, number)));
        }

        foreach ((XElement require, int number) in blocks.Where(b => SupportsVulkan((string?)b.Require.Attribute("api"))))
        {
            foreach (XElement item in require.Elements())
            {
                string? name = (string?)item.Attribute("name");
                switch (item.Name.LocalName)
                {
                    case "type" when name is not null:
                        _requiredTypes.Add(name);
                        break;
                    case "command" when name is not null:
                        _requiredCommands.Add(name);
                        break;
                    case "enum" when (string?)item.Attribute("extends") is { } extends && (string?)item.Attribute("alias") is null:
                        if (!_enums.TryGetValue(extends, out EnumInfo? info))
                        {
                            info = new EnumInfo { IsBitmask = item.Attribute("bitpos") is not null };
                            _enums[extends] = info;
                        }

                        if (!info.Members.Any(m => m.Name == name))
                        {
                            info.Members.Add((name!, MemberValue(item, number)));
                        }

                        break;
                }
            }
        }
    }

    // ---- Enums and handles -------------------------------------------------------------------------------------

    private string Resolve(string name) => _aliases.TryGetValue(name, out string? target) ? Resolve(target) : name;

    private void EmitEnums()
    {
        foreach (string name in _requiredTypes.Where(t => _enums.ContainsKey(t) && !_aliases.ContainsKey(t)).Order(StringComparer.Ordinal))
        {
            EnumInfo info = _enums[name];
            string underlying = info.BitWidth == 64 ? "ulong" : info.IsBitmask ? "uint" : "int";
            var model = new EnumModel { Name = name, Underlying = underlying, IsFlags = info.IsBitmask };

            string prefix = EnumPrefix(name, out string tag);
            var used = new HashSet<string>();
            foreach ((string memberName, BigInteger value) in info.Members.DistinctBy(m => m.Name))
            {
                string csName = Naming.Member(MemberName(memberName, prefix, tag));
                while (!used.Add(csName))
                {
                    csName += "_";
                }

                BigInteger shown = value;
                if (info.IsBitmask && value < 0)
                {
                    continue;
                }

                if ((underlying == "uint" && shown > uint.MaxValue) || (underlying == "int" && (shown > int.MaxValue || shown < int.MinValue)))
                {
                    continue;
                }

                model.Members.Add(new EnumMemberModel(csName, shown.ToString(CultureInfo.InvariantCulture)));
                if (name == "VkStructureType")
                {
                    _structureTypes[memberName] = csName;
                }
            }

            _model.Enums.Add(model);
        }

        _model.Enums.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    /// <summary><c>VkImageViewType</c> → <c>VK_IMAGE_VIEW_TYPE_</c>; flag bits lose <c>FlagBits</c>; a vendor tag is split off.</summary>
    private string EnumPrefix(string typeName, out string tag)
    {
        tag = "";
        string baseName = typeName;
        foreach (string candidate in _tags)
        {
            if (baseName.EndsWith(candidate, StringComparison.Ordinal) && baseName.Length > candidate.Length && char.IsLower(baseName[^(candidate.Length + 1)]))
            {
                tag = candidate;
                baseName = baseName[..^candidate.Length];
                break;
            }
        }

        baseName = baseName.Replace("FlagBits", "", StringComparison.Ordinal);
        string snake = Regex.Replace(baseName, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[a-z])(?=[0-9]+$)", "_").ToUpperInvariant();
        return snake + "_";
    }

    private static string MemberName(string memberName, string prefix, string tag)
    {
        string name = memberName;
        if (tag.Length > 0 && name.EndsWith("_" + tag, StringComparison.Ordinal))
        {
            name = name[..^(tag.Length + 1)];
        }

        name = name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length ? name[prefix.Length..] : name["VK_".Length..];
        if (name.EndsWith("_BIT", StringComparison.Ordinal) && name.Length > 4)
        {
            name = name[..^4];
        }

        return string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(Word));
    }

    /// <summary><c>R8G8B8A8</c> → <c>R8G8B8A8</c>, <c>UNORM</c> → <c>Unorm</c>: letters after a digit stay upper case.</summary>
    private static string Word(string word)
    {
        var text = new StringBuilder(word.Length);
        for (int i = 0; i < word.Length; i++)
        {
            bool upper = i == 0 || char.IsDigit(word[i - 1]);
            text.Append(upper ? char.ToUpperInvariant(word[i]) : char.ToLowerInvariant(word[i]));
        }

        return text.ToString();
    }

    private void EmitHandles()
    {
        foreach (string name in _requiredTypes.Where(_handles.ContainsKey).Order(StringComparer.Ordinal))
        {
            _model.Handles.Add(new HandleModel(name, _handles[name]));
        }
    }

    // ---- Structs and unions ------------------------------------------------------------------------------------

    private void EmitStructs()
    {
        var names = _requiredTypes.Where(t => _types.TryGetValue(t, out XElement? x) && (string?)x.Attribute("category") is "struct" or "union").Order(StringComparer.Ordinal).ToList();

        // A struct that cannot be built takes every struct that uses it down with it, so build until nothing new fails.
        int failures;
        do
        {
            failures = _failed.Count;
            _model.Structs.Clear();
            _model.InlineArrays.Clear();
            foreach (string name in names.Where(n => !_failed.Contains(n)))
            {
                try
                {
                    _model.Structs.Add(BuildStruct(_types[name]));
                }
                catch (UnsupportedException e)
                {
                    _failed.Add(name);
                    _model.Skipped.Add($"struct {name}: {e.Message}");
                }
            }
        }
        while (_failed.Count != failures);

        ApplyClangSizes();
    }

    private StructModel BuildStruct(XElement type)
    {
        string name = NameOf(type)!;
        bool isUnion = (string?)type.Attribute("category") == "union";
        var model = new StructModel { Name = name, HasOverlap = isUnion };

        long offset = 0;
        int bitsInUnit = 0;
        int bitUnits = 0;
        foreach (XElement member in type.Elements("member").Where(m => SupportsVulkan((string?)m.Attribute("api"))))
        {
            Declaration declaration = Parse(member);
            if (declaration.Bits > 0)
            {
                bitsInUnit += declaration.Bits;
                if (bitsInUnit % 32 == 0)
                {
                    model.Fields.Add(new FieldModel("BitField" + bitUnits++, "uint", offset, 0));
                    bitsInUnit = 0;
                }

                continue;
            }

            if (declaration.Name == "sType" && (string?)member.Attribute("values") is { } values)
            {
                // A struct that several structure types name (an alias) takes the first.
                string structureType = values.Split(',')[0];
                if (_structureTypes.TryGetValue(structureType, out string? structureTypeMember))
                {
                    model.StructureType = structureTypeMember;
                }
                else if (!_model.Skipped.Contains($"struct {name}: {structureType} is not in VkStructureType, so sType is not set by new()"))
                {
                    _model.Skipped.Add($"struct {name}: {structureType} is not in VkStructureType, so sType is not set by new()");
                }
            }

            string fieldName = Naming.Member(Naming.Capitalize(declaration.Name));
            if (fieldName == name || fieldName is "Equals" or "GetHashCode" or "ToString" or "GetType")
            {
                fieldName += "_";
            }

            (string csType, int fixedLength) = MapFieldType(declaration);
            model.Fields.Add(new FieldModel(fieldName, csType, isUnion ? 0 : offset, fixedLength));
        }

        if (bitsInUnit != 0)
        {
            throw new UnsupportedException("bit fields that do not fill 32 bits");
        }

        return model;
    }

    /// <summary>One <c>&lt;member&gt;</c> or <c>&lt;param&gt;</c>: its type name, pointer depth, array sizes and bit width.</summary>
    private sealed record Declaration(string TypeName, int Pointers, string Name, List<int> ArraySizes, int Bits);

    private Declaration Parse(XElement element)
    {
        string typeName = element.Element("type")!.Value;
        string name = element.Element("name")!.Value;

        // The text between the type and the name holds the pointers; the text after the name holds [array] or :bits.
        var before = new StringBuilder();
        var after = new StringBuilder();
        bool seenName = false;
        bool seenType = false;
        foreach (XNode node in element.Nodes())
        {
            if (node is XElement child)
            {
                if (child.Name == "name")
                {
                    seenName = true;
                }
                else if (child.Name == "type")
                {
                    seenType = true;
                }
                else if (child.Name == "enum" && seenName)
                {
                    after.Append(_constants.TryGetValue(child.Value, out BigInteger value) ? value.ToString(CultureInfo.InvariantCulture) : "?");
                }
            }
            else if (node is XText text)
            {
                (seenName ? after : seenType ? before : new StringBuilder()).Append(text.Value);
            }
        }

        var sizes = Regex.Matches(after.ToString(), @"\[([^\]]*)\]").Select(m => int.TryParse(m.Groups[1].Value, out int n) ? n : 0).ToList();
        if (sizes.Contains(0))
        {
            throw new UnsupportedException($"array size of {name}");
        }

        int bits = Regex.Match(after.ToString(), @":\s*(\d+)") is { Success: true } b ? int.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return new Declaration(typeName, before.ToString().Count(c => c == '*'), name, sizes, bits);
    }

    private (string Type, int FixedLength) MapFieldType(Declaration declaration)
    {
        string element = MapType(declaration.TypeName, declaration.Pointers);
        if (declaration.ArraySizes.Count == 0)
        {
            return (element, 0);
        }

        int total = declaration.ArraySizes.Aggregate(1, (a, b) => a * b);
        if (declaration.Pointers == 0 && element is "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double")
        {
            return (element, total);
        }

        string type = element;
        foreach (int size in declaration.ArraySizes.AsEnumerable().Reverse())
        {
            type = InlineArray(type, size);
        }

        return (type, 0);
    }

    private string InlineArray(string elementType, int length)
    {
        string name = $"InlineArray{length}_{Sanitize(elementType)}";
        if (!_model.InlineArrays.ContainsKey(name))
        {
            bool pointer = elementType.EndsWith('*') || elementType.StartsWith("delegate*", StringComparison.Ordinal);
            _model.InlineArrays.Add(name, new InlineArrayModel(name, pointer ? "nint" : elementType, length));
        }

        return name;
    }

    private static string Sanitize(string type)
    {
        if (type.StartsWith("delegate*", StringComparison.Ordinal))
        {
            uint hash = 2166136261;
            foreach (char c in type)
            {
                hash = (hash ^ c) * 16777619;
            }

            return "Fn" + hash.ToString("x8", CultureInfo.InvariantCulture);
        }

        return type.Replace("*", "Ptr", StringComparison.Ordinal);
    }

    // ---- Types -------------------------------------------------------------------------------------------------

    private string MapType(string typeName, int pointers)
    {
        string baseType = MapBase(typeName, pointers > 0);
        return baseType + new string('*', pointers);
    }

    private string MapBase(string typeName, bool behindPointer)
    {
        typeName = Resolve(typeName);
        if (_failed.Contains(typeName))
        {
            throw new UnsupportedException($"uses {typeName}, which was skipped");
        }

        if (s_primitives.TryGetValue(typeName, out string? primitive))
        {
            return primitive;
        }

        if (_bitmasks.TryGetValue(typeName, out string? bits))
        {
            return bits.Length > 0 && _requiredTypes.Contains(Resolve(bits)) ? Resolve(bits) : _basetypes.GetValueOrDefault(typeName, "uint");
        }

        if (_basetypes.TryGetValue(typeName, out string? basetype))
        {
            return basetype;
        }

        if (_handles.ContainsKey(typeName))
        {
            return typeName;
        }

        if (_funcPointers.ContainsKey(typeName))
        {
            return FunctionPointer(_types[typeName]);
        }

        if (_types.TryGetValue(typeName, out XElement? type))
        {
            switch ((string?)type.Attribute("category"))
            {
                case "enum" when _enums.ContainsKey(typeName):
                case "struct":
                case "union":
                    return typeName;
            }
        }

        // A type from a header outside the registry (a window system's, a video codec's): only usable behind a pointer.
        return behindPointer ? "void" : throw new UnsupportedException($"type {typeName} is not defined by the registry");
    }

    private string FunctionPointer(XElement type)
    {
        var parts = new List<string>();
        foreach (XElement param in type.Elements("param"))
        {
            Declaration declaration = Parse(param);
            parts.Add(MapType(declaration.TypeName, declaration.Pointers));
        }

        XElement proto = type.Element("proto")!;
        parts.Add(MapType(proto.Element("type")!.Value, proto.Value.Count(c => c == '*')));
        return $"delegate* {Cdecl}<{string.Join(", ", parts)}>";
    }

    // ---- Commands ----------------------------------------------------------------------------------------------

    private void EmitCommands()
    {
        foreach (string requested in _requiredCommands.Distinct().Order(StringComparer.Ordinal))
        {
            string name = Resolve(requested);
            if (!_commands.TryGetValue(name, out XElement? command) || name == "vkGetInstanceProcAddr")
            {
                continue;
            }

            try
            {
                XElement proto = command.Element("proto")!;
                string returnType = MapType(proto.Element("type")!.Value, proto.Value.Count(c => c == '*'));
                var parameters = command.Elements("param")
                    .Where(p => SupportsVulkan((string?)p.Attribute("api")))
                    .Select(Parse)
                    .Select(d => new ParameterModel(d.ArraySizes.Count > 0 ? MapType(d.TypeName, d.Pointers + 1) : MapType(d.TypeName, d.Pointers), d.Name))
                    .ToList();

                string first = parameters.Count > 0 ? command.Elements("param").First().Element("type")!.Value : "";
                CommandLevel level = s_globalCommands.Contains(requested) ? CommandLevel.Global
                    : requested == "vkGetDeviceProcAddr" || first is "VkInstance" or "VkPhysicalDevice" ? CommandLevel.Instance
                    : CommandLevel.Device;
                var model = new CommandModel { CName = requested, Name = requested["vk".Length..], ReturnType = returnType, Level = level };
                model.Parameters.AddRange(parameters);
                _model.Commands.Add(model);
            }
            catch (UnsupportedException e)
            {
                _model.Skipped.Add($"command {requested}: {e.Message}");
            }
        }

        // vkGetInstanceProcAddr is the one function the loader library exports, and the root of all the others.
        XElement getProc = _commands["vkGetInstanceProcAddr"];
        var import = new FunctionModel
        {
            CName = "vkGetInstanceProcAddr",
            Name = "GetInstanceProcAddr",
            ReturnType = FunctionPointer(_types["PFN_vkVoidFunction"]),
        };
        foreach (XElement param in getProc.Elements("param"))
        {
            Declaration declaration = Parse(param);
            import.Parameters.Add(new ParameterModel(MapType(declaration.TypeName, declaration.Pointers), declaration.Name));
        }

        _model.Functions.Add(import);
        _model.Commands.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    // ---- Layout ------------------------------------------------------------------------------------------------

    /// <summary>The sizes clang computes for the structs in vulkan_core.h, for the layout test.</summary>
    private void ApplyClangSizes()
    {
        string header = Path.Combine(_root, "ThirdParty", "vulkan", "include", "vulkan", "vulkan_core.h");
        using CXIndex index = CXIndex.Create();
        CXErrorCode error = CXTranslationUnit.TryParse(
            index, header, ["-x", "c", "-std=c11", "-resource-dir", HeaderParser.ClangResourceDirectory()], [],
            CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies, out CXTranslationUnit unit);
        if (error != CXErrorCode.CXError_Success)
        {
            throw new InvalidOperationException($"libclang could not parse {header}: {error}");
        }

        using (unit)
        {
            var sizes = new Dictionary<string, long>();
            foreach (CXCursor cursor in unit.Cursor.Children())
            {
                if (cursor.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl && cursor.IsDefinition)
                {
                    sizes[cursor.Spelling.ToString()] = cursor.Type.SizeOf;
                }
            }

            foreach (StructModel structure in _model.Structs.Where(s => sizes.ContainsKey(s.Name)))
            {
                structure.Size = sizes[structure.Name];
            }
        }
    }

    private sealed class UnsupportedException(string message) : Exception(message);
}
