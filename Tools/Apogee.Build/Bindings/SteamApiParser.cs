using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClangSharp.Interop;

namespace Apogee.Build.Bindings;

/// <summary>
/// Reads the Steamworks flat API into an <see cref="ApiModel"/>. The types come from Valve's <c>steam_api.json</c> (enums, structs, callback structs with their ids, constants, typedefs); the
/// functions are the <c>S_API</c> declarations of <c>steam_api_flat.h</c>, which are regular enough to read as text; and the sizes of the structs, which the JSON does not have, are measured by
/// libclang parsing the SDK's headers as C++, once with Linux's callback packing (4 bytes) and once with Windows' (8). A struct whose size differs is declared with
/// <c>Pack = SteamPack.Callback</c>, which the project defines per operating system. Only the interfaces and functions the config names are bound.
/// </summary>
public sealed partial class SteamApiParser
{
    private static readonly Dictionary<string, string> s_builtin = new(StringComparer.Ordinal)
    {
        ["void"] = "void",
        ["bool"] = "CBool",
        ["char"] = "byte",
        ["signed char"] = "sbyte",
        ["unsigned char"] = "byte",
        ["short"] = "short",
        ["unsigned short"] = "ushort",
        ["int"] = "int",
        ["unsigned int"] = "uint",
        ["long long"] = "long",
        ["unsigned long long"] = "ulong",
        ["float"] = "float",
        ["double"] = "double",
        ["size_t"] = "nuint",
        ["intptr_t"] = "nint",
        ["uintptr_t"] = "nuint",
        ["uint8"] = "byte",
        ["int8"] = "sbyte",
        ["uint16"] = "ushort",
        ["int16"] = "short",
        ["uint32"] = "uint",
        ["int32"] = "int",
        ["uint64"] = "ulong",
        ["int64"] = "long",
        ["uint64_steamid"] = "ulong",
        ["uint64_gameid"] = "ulong",
        ["CSteamID"] = "ulong",
        ["CGameID"] = "ulong",
    };

    private static readonly HashSet<string> s_fixedElements = ["byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double"];

    private readonly BindingConfig _config;
    private readonly string _root;
    private readonly ApiModel _model = new();
    private readonly Dictionary<string, string> _typedefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _enums = new(StringComparer.Ordinal);       // C name → C# name
    private readonly Dictionary<string, JsonElement> _structs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _callbackIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _interfaces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _neededStructs = new(StringComparer.Ordinal);
    private readonly List<string> _structOrder = [];
    private readonly HashSet<string> _bound = new(StringComparer.Ordinal);                   // C# type names already emitted
    private readonly HashSet<string> _usedFunctionNames = new(StringComparer.Ordinal);

    public SteamApiParser(BindingConfig config, string repositoryRoot)
    {
        _config = config;
        _root = repositoryRoot;
    }

    public ApiModel Parse()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, _config.SteamApi)));
        JsonElement api = document.RootElement;

        foreach (JsonElement typedef in api.GetProperty("typedefs").EnumerateArray())
        {
            _typedefs[typedef.GetProperty("typedef").GetString()!] = typedef.GetProperty("type").GetString()!;
        }

        foreach (JsonElement structure in api.GetProperty("structs").EnumerateArray())
        {
            _structs[structure.GetProperty("struct").GetString()!] = structure;
        }

        // The manual-dispatch message is declared in steam_api_internal.h and is not in the JSON.
        _structs["CallbackMsg_t"] = JsonDocument.Parse("""
            {"struct":"CallbackMsg_t","fields":[
              {"fieldname":"m_hSteamUser","fieldtype":"HSteamUser"},{"fieldname":"m_iCallback","fieldtype":"int"},
              {"fieldname":"m_pubParam","fieldtype":"uint8 *"},{"fieldname":"m_cubParam","fieldtype":"int"}]}
            """).RootElement.Clone();

        foreach (JsonElement callback in api.GetProperty("callback_structs").EnumerateArray())
        {
            string name = callback.GetProperty("struct").GetString()!;
            _structs[name] = callback;
            _callbackIds[name] = callback.GetProperty("callback_id").GetInt32();
        }

        foreach (JsonElement @interface in api.GetProperty("interfaces").EnumerateArray())
        {
            _interfaces.Add(@interface.GetProperty("classname").GetString()!);
        }

        foreach (JsonElement enumeration in api.GetProperty("enums").EnumerateArray())
        {
            AddEnum(enumeration);
        }

        AddConstants(api.GetProperty("consts"));
        AddFunctions();
        AddStructs();
        MeasureLayouts();
        return _model;
    }

    // ---- Names ------------------------------------------------------------------------------------------------------------------

    /// <summary>A Steam type name as C# has it: <c>AppId_t</c> is <c>AppId</c>; interfaces and enums keep theirs.</summary>
    private static string TypeName(string cName) => cName.EndsWith("_t", StringComparison.Ordinal) && cName.Length > 2 ? cName[..^2] : cName;

    private static string FieldName(string cName, HashSet<string> used)
    {
        string name = cName.StartsWith("m_", StringComparison.Ordinal) ? cName[2..] : cName;
        Match hungarian = HungarianPrefix().Match(name); // eResult → Result, rgubIPv6 → IPv6, unAppID → AppID
        if (hungarian.Success && hungarian.Length < name.Length)
        {
            name = name[hungarian.Length..];
        }

        string stripped = Naming.Member(Naming.Capitalize(name));
        if (used.Contains(stripped)) // m_pubParam and m_cubParam both strip to Param: keep the second one's prefix
        {
            stripped = Naming.Member(Naming.Capitalize(cName.StartsWith("m_", StringComparison.Ordinal) ? cName[2..] : cName));
        }

        string result = stripped;
        while (!used.Add(result))
        {
            result += "_";
        }

        return result;
    }

    [GeneratedRegex("^[a-z]+(?=[A-Z0-9])")]
    private static partial Regex HungarianPrefix();

    // ---- Enums and constants ----------------------------------------------------------------------------------------------------

    private void AddEnum(JsonElement enumeration)
    {
        string cName = enumeration.GetProperty("enumname").GetString()!;
        string name = TypeName(cName);
        _enums[cName] = name;

        var members = enumeration.GetProperty("values").EnumerateArray().Select(v => (Name: v.GetProperty("name").GetString()!, Value: v.GetProperty("value").GetString()!)).ToList();
        long min = 0, max = 0;
        var values = new List<long>();
        foreach ((_, string value) in members)
        {
            long parsed = ParseNumber(value) ?? 0;
            values.Add(parsed);
            min = Math.Min(min, parsed);
            max = Math.Max(max, parsed);
        }

        string underlying = min < 0 ? (min >= int.MinValue && max <= int.MaxValue ? "int" : "long") : max <= uint.MaxValue ? "uint" : "ulong";
        var model = new EnumModel { Name = name, Underlying = underlying };
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < members.Count; i++)
        {
            string member = members[i].Name;
            if (member.StartsWith("k_" + cName, StringComparison.Ordinal) && member.Length > cName.Length + 2)
            {
                member = member[(cName.Length + 2)..];
            }
            else if (member.StartsWith("k_", StringComparison.Ordinal))
            {
                member = member[2..];
            }

            member = Naming.Member(Naming.Capitalize(member.TrimStart('_')));
            while (!used.Add(member))
            {
                member += "_";
            }

            model.Members.Add(new EnumMemberModel(member, values[i].ToString(CultureInfo.InvariantCulture)));
        }

        _model.Enums.Add(model);
    }

    /// <summary>A number, or a left-to-right chain of numbers joined by <c>+ * |</c> (<c>128 + 1</c>, <c>100 * 1024 * 1024</c>), or a negative one written <c>- 1</c>.</summary>
    private static long? ParseNumber(string text)
    {
        text = Regex.Replace(text.Trim(), @"^-\s+", "-");
        Match chain = Regex.Match(text, @"^(?<first>-?\w+)(?:\s*(?<op>[*+|])\s*(?<rest>\w+))+$");
        if (chain.Success)
        {
            long? total = ParseNumber(chain.Groups["first"].Value);
            for (int i = 0; i < chain.Groups["op"].Captures.Count && total is not null; i++)
            {
                long? operand = ParseNumber(chain.Groups["rest"].Captures[i].Value);
                total = operand is null ? null : chain.Groups["op"].Captures[i].Value switch
                {
                    "*" => total * operand,
                    "+" => total + operand,
                    _ => total | operand,
                };
            }

            return total;
        }

        text = text.TrimEnd('u', 'U', 'l', 'L');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex) ? hex : null;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    private void AddConstants(JsonElement constants)
    {
        foreach (JsonElement constant in constants.EnumerateArray())
        {
            string name = constant.GetProperty("constname").GetString()!;
            string type = constant.GetProperty("consttype").GetString()!;
            string value = constant.GetProperty("constval").GetString()!;
            string bare = name.StartsWith("k_", StringComparison.Ordinal) ? name[2..] : name;
            bare = Regex.Replace(bare, "^(c?u|n|ul|ub|us|un|i|f|e|h|ch)(?=[A-Z])", string.Empty);
            if (value.Length > 1 && value[0] == '"')
            {
                _model.Skipped.Add($"constant {name}: a string");
                continue;
            }

            long? number = ParseNumber(value);
            if (number is null)
            {
                if (ulong.TryParse(value.Trim().TrimEnd('u', 'U', 'l', 'L').Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong big) && value.Contains("0x", StringComparison.OrdinalIgnoreCase))
                {
                    _model.Constants.Add(new ConstantModel(Naming.Member(Naming.Capitalize(bare)), "ulong", big.ToString(CultureInfo.InvariantCulture)));
                    continue;
                }

                _model.Skipped.Add($"constant {name}: the value {value} is not a plain number");
                continue;
            }

            string resolved = Resolve(type);
            string csType = resolved is "ulong" or "long" ? resolved : number > uint.MaxValue ? "long" : number > int.MaxValue ? "uint" : "int";
            _model.Constants.Add(new ConstantModel(Naming.Member(Naming.Capitalize(bare)), csType == "ulong" && number < 0 ? "long" : csType, number.Value.ToString(CultureInfo.InvariantCulture)));
        }

        // Duplicate names (k_cchPersonaNameMax and k_cchMaxRichPresenceValueLength differ, but prefixes can collide): keep the first.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _model.Constants.RemoveAll(c => !seen.Add(c.Name));
    }

    // ---- Types ------------------------------------------------------------------------------------------------------------------

    /// <summary>The C# primitive a typedef chain ends in (<c>AppId_t</c> → <c>uint</c>), or the name itself when it is not one.</summary>
    private string Resolve(string cName)
    {
        for (int guard = 0; guard < 8; guard++)
        {
            if (s_builtin.TryGetValue(cName, out string? builtin))
            {
                return builtin;
            }

            if (!_typedefs.TryGetValue(cName, out string? next))
            {
                break;
            }

            // A typedef of an array (SteamErrMsg is char[1024]) is passed as a pointer to its first element.
            Match array = Regex.Match(next, @"^(?<element>.+?)\s*\[\d+\]$");
            cName = array.Success ? array.Groups["element"].Value.Trim() : next;
        }

        return cName;
    }

    /// <summary>The C# type of a C++ type spelled the way Valve's files spell it, or null if it cannot be bound (a function pointer, a class we do not know).</summary>
    private string? MapType(string spelling)
    {
        string t = spelling.Trim();
        int pointers = t.Count(c => c == '*') + t.Count(c => c == '&');
        t = t.Replace("*", " ", StringComparison.Ordinal).Replace("&", " ", StringComparison.Ordinal);
        t = Regex.Replace(t, @"\b(const|struct|enum|class|volatile)\b", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Contains('(', StringComparison.Ordinal) || t.Contains('<', StringComparison.Ordinal))
        {
            return null;
        }

        string? element;
        if (_enums.TryGetValue(t, out string? enumName))
        {
            element = enumName;
        }
        else if (_interfaces.Contains(t))
        {
            element = t;
            NeedInterface(t);
        }
        else if (_structs.ContainsKey(t))
        {
            element = TypeName(t);
            Need(t);
        }
        else
        {
            string resolved = Resolve(t);
            if (_enums.TryGetValue(resolved, out string? viaTypedef))
            {
                element = viaTypedef;
            }
            else if (s_builtin.ContainsValue(resolved))
            {
                element = resolved;
            }
            else if (_structs.ContainsKey(resolved))
            {
                element = TypeName(resolved);
                Need(resolved);
            }
            else
            {
                return null; // a class or callback type we do not bind
            }
        }

        if (element == "void" && pointers == 0)
        {
            return "void";
        }

        return element + new string('*', pointers);
    }

    private void NeedInterface(string name)
    {
        if (_bound.Add(name))
        {
            _model.Structs.Add(new StructModel { Name = name }); // an opaque handle: Steam hands out the pointer and keeps the object
        }
    }

    private void Need(string cName)
    {
        if (_neededStructs.Add(cName))
        {
            _structOrder.Add(cName);
        }
    }

    // ---- Functions --------------------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"^S_API\s+(?<ret>.+?)\s*(?:S_CALLTYPE\s+)?(?<name>SteamAPI_\w+|SteamGameServer\w*|Steam\w+)\s*\((?<params>.*)\)\s*;", RegexOptions.Singleline)]
    private static partial Regex FlatDeclaration();

    private void AddFunctions()
    {
        var interfaceNames = _config.Interfaces.ToHashSet(StringComparer.Ordinal);
        Regex[] include = [.. _config.IncludeFunctions.Select(p => new Regex(p, RegexOptions.CultureInvariant))];

        // The flat interface functions, and the few core ones (init, shutdown, manual dispatch) in the other public headers.
        string folder = Path.GetDirectoryName(Path.Combine(_root, _config.FlatHeader))!;
        string[] files = [Path.Combine(_root, _config.FlatHeader), .. new[] { "steam_api.h", "steam_api_common.h", "steam_api_internal.h" }.Select(f => Path.Combine(folder, f))];
        foreach (string line in files.Where(File.Exists).SelectMany(f => File.ReadAllText(f).Split('\n')))
        {
            if (!line.StartsWith("S_API", StringComparison.Ordinal))
            {
                continue;
            }

            Match match = FlatDeclaration().Match(line.Trim());
            if (!match.Success)
            {
                _model.Skipped.Add($"function: cannot read {line.Trim()}");
                continue;
            }

            string cName = match.Groups["name"].Value;
            Match owner = Regex.Match(cName, @"^SteamAPI_(?<i>I\w+?)_[A-Z]\w*$");
            bool bound = owner.Success ? interfaceNames.Contains(owner.Groups["i"].Value) : include.Any(r => r.IsMatch(cName));
            if (!bound)
            {
                continue;
            }

            string? returnType = MapType(match.Groups["ret"].Value);
            if (returnType is null)
            {
                _model.Skipped.Add($"function {cName}: the return type {match.Groups["ret"].Value} cannot be bound");
                continue;
            }

            var parameters = new List<ParameterModel>();
            bool ok = true;
            string parameterText = match.Groups["params"].Value.Trim();
            foreach (string parameter in parameterText.Length == 0 || parameterText == "void" ? [] : SplitParameters(parameterText))
            {
                Match parts = Regex.Match(parameter.Trim(), @"^(?<type>.+?[\s\*&])(?<name>\w+)$", RegexOptions.Singleline);
                string? type = parts.Success ? MapType(parts.Groups["type"].Value) : null;
                if (type is null)
                {
                    _model.Skipped.Add($"function {cName}: the parameter '{parameter.Trim()}' cannot be bound");
                    ok = false;
                    break;
                }

                parameters.Add(new ParameterModel(type, Naming.Parameter(parts.Groups["name"].Value)));
            }

            if (!ok)
            {
                continue;
            }

            string name = Naming.Pascal(owner.Success && cName.StartsWith("SteamAPI_ISteam", StringComparison.Ordinal) ? cName["SteamAPI_ISteam".Length..]
                : owner.Success && cName.StartsWith("SteamAPI_I", StringComparison.Ordinal) ? cName["SteamAPI_I".Length..]
                : cName.StartsWith("SteamAPI_", StringComparison.Ordinal) ? cName["SteamAPI_".Length..] : cName);
            while (!_usedFunctionNames.Add(name))
            {
                name += "_";
            }

            var function = new FunctionModel { CName = cName, Name = name, ReturnType = returnType };
            function.Parameters.AddRange(parameters);
            _model.Functions.Add(function);
        }
    }

    private static List<string> SplitParameters(string text)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '(' or '<')
            {
                depth++;
            }
            else if (text[i] is ')' or '>')
            {
                depth--;
            }
            else if (text[i] == ',' && depth == 0)
            {
                result.Add(text[start..i]);
                start = i + 1;
            }
        }

        result.Add(text[start..]);
        return result;
    }

    // ---- Structs ----------------------------------------------------------------------------------------------------------------

    private void AddStructs()
    {
        // The callback structs that belong to the bound interfaces' domain are bound whole: they are what the callbacks deliver. Binding them pulls in the structs they hold.
        foreach ((string name, int id) in _callbackIds)
        {
            if (CallbackWanted(name))
            {
                Need(name);
            }
        }

        // Walk the closure: a struct's fields may need more structs.
        for (int i = 0; i < _structOrder.Count; i++)
        {
            string cName = _structOrder[i];
            if (!_structs.TryGetValue(cName, out JsonElement element))
            {
                continue;
            }

            string name = TypeName(cName);
            if (!_bound.Add(name))
            {
                continue;
            }

            var model = new StructModel { Name = name };
            var used = new HashSet<string>(StringComparer.Ordinal);
            bool ok = true;
            foreach (JsonElement field in element.GetProperty("fields").EnumerateArray())
            {
                string fieldType = field.GetProperty("fieldtype").GetString()!;
                string fieldName = FieldName(field.GetProperty("fieldname").GetString()!, used);
                if (fieldName == name)
                {
                    // A member cannot have the name of its type (CheckFileSignature_t.m_eCheckFileSignature).
                    fieldName += "Value";
                    used.Add(fieldName);
                }

                Match array = Regex.Match(fieldType, @"^(?<element>.+?)\s*\[(?<length>\d+)\]$");
                if (array.Success)
                {
                    string? elementType = MapType(array.Groups["element"].Value);
                    int length = int.Parse(array.Groups["length"].Value, CultureInfo.InvariantCulture);
                    if (elementType is null || elementType.Contains('*', StringComparison.Ordinal))
                    {
                        ok = false;
                        _model.Skipped.Add($"struct {cName}: the field {fieldName} ({fieldType}) cannot be bound");
                        break;
                    }

                    if (s_fixedElements.Contains(elementType))
                    {
                        model.Fields.Add(new FieldModel(fieldName, elementType, 0, length));
                    }
                    else
                    {
                        string inline = $"InlineArray{length}_{elementType}";
                        _model.InlineArrays[inline] = new InlineArrayModel(inline, elementType, length);
                        model.Fields.Add(new FieldModel(fieldName, inline, 0, 0));
                    }

                    continue;
                }

                string? type = MapType(fieldType);
                if (type is null)
                {
                    ok = false;
                    _model.Skipped.Add($"struct {cName}: the field {fieldName} ({fieldType}) cannot be bound");
                    break;
                }

                model.Fields.Add(new FieldModel(fieldName, type, 0, 0));
            }

            if (!ok)
            {
                _bound.Remove(name);
                continue;
            }

            _model.Structs.Add(model);
        }

        foreach ((string name, int id) in _callbackIds)
        {
            if (_bound.Contains(TypeName(name)) && CallbackWanted(name))
            {
                _model.Constants.Add(new ConstantModel(TypeName(name) + "Id", "int", id.ToString(CultureInfo.InvariantCulture)));
            }
        }

        // A struct that was needed but could not be bound leaves its users with a dangling name: report them.
        foreach (string cName in _structOrder)
        {
            if (!_bound.Contains(TypeName(cName)))
            {
                _model.Skipped.Add($"struct {cName}: not bound, so what uses it does not compile");
            }
        }
    }

    // The callbacks of the interfaces we bind: those whose struct name is in the closure of their domain. The JSON does not say which interface sends which, so the config's
    // interfaces decide by header: a callback struct is wanted when its name appears in one of the interface headers named by the interface (isteamuser.h for ISteamUser…).
    private bool CallbackWanted(string cName)
    {
        _callbackHeaders ??= LoadCallbackHeaders();
        return _callbackHeaders.Contains(cName);
    }

    private HashSet<string>? _callbackHeaders;

    private HashSet<string> LoadCallbackHeaders()
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        string folder = Path.GetDirectoryName(Path.Combine(_root, _config.FlatHeader))!;
        foreach (string interfaceName in _config.Interfaces)
        {
            string file = Path.Combine(folder, interfaceName.ToLowerInvariant() + ".h");
            if (!File.Exists(file))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"struct\s+(?<name>\w+_t)\s*\r?\n?\s*\{\s*(//[^\n]*\n\s*)*enum\s*\{\s*k_iCallback"))
            {
                wanted.Add(match.Groups["name"].Value);
            }
        }

        // Callbacks that belong to no one interface but every program wants, from the config's function list (steam_api_common.h, steamclientpublic.h).
        foreach (string common in new[] { "SteamServersConnected_t", "SteamServerConnectFailure_t", "SteamServersDisconnected_t", "GameOverlayActivated_t", "SteamShutdown_t", "SteamAPICallCompleted_t", "IPCFailure_t" })
        {
            wanted.Add(common);
        }

        return wanted;
    }

    // ---- Layout -----------------------------------------------------------------------------------------------------------------

    /// <summary>The size of every bound struct, as clang lays it out in C++ with Linux's callback packing and with Windows'.</summary>
    private void MeasureLayouts()
    {
        Dictionary<string, (long Size, long Align)> small = Measure(windows: false);
        Dictionary<string, (long Size, long Align)> large = Measure(windows: true);
        var cNames = _structs.Keys.ToDictionary(TypeName, k => k, StringComparer.Ordinal);
        foreach (StructModel structure in _model.Structs.Where(s => s.Fields.Count > 0))
        {
            string cName = cNames.GetValueOrDefault(structure.Name, structure.Name);
            if (!small.TryGetValue(cName, out (long Size, long Align) measured))
            {
                _model.Skipped.Add($"struct {cName}: its size could not be measured");
                continue;
            }

            structure.Size = measured.Size;
            if (large.TryGetValue(cName, out (long Size, long Align) other) && other.Size != measured.Size)
            {
                structure.PackDependent = true;
                structure.LargePackSize = other.Size;
            }
            else if (measured.Align is 1 or 2 or 4 && structure.Fields.Any(f => NaturalAlignment(f.Type) > measured.Align))
            {
                // Packed on every platform (isteamfriends.h and others say #pragma pack(push, 4) themselves; a few are 2): a plain C# struct would align its wide members naturally.
                structure.FixedPack = (int)measured.Align;
            }
        }
    }

    /// <summary>The alignment of a field's type in an unpacked struct: exact for the primitives, 8 for everything else (the wide ones, and structs and enums that may contain them).</summary>
    private static int NaturalAlignment(string type) => type switch
    {
        "byte" or "sbyte" or "bool" => 1,
        "short" or "ushort" => 2,
        "int" or "uint" or "float" => 4,
        _ => 8,
    };

    private Dictionary<string, (long Size, long Align)> Measure(bool windows)
    {
        string include = Path.GetFullPath(Path.Combine(_root, Path.GetDirectoryName(_config.FlatHeader)!, ".."));
        string directory = Path.Combine(_root, "Bin", "bindings");
        string umbrella = Path.Combine(directory, _config.Name + (windows ? ".large" : ".small") + ".cpp");
        File.WriteAllText(umbrella, "#include <steam/steam_api.h>\n#include <steam/steam_api_flat.h>\n");
        var args = new List<string> { "-x", "c++", "-std=c++17", "-resource-dir", HeaderParser.ClangResourceDirectory(), "-I" + include };
        if (windows)
        {
            args.Add("-U__linux__"); // the header chooses 8-byte callback packing where it is not Linux, macOS or FreeBSD
        }

        using CXIndex index = CXIndex.Create();
        CXErrorCode error = CXTranslationUnit.TryParse(index, umbrella, args.ToArray(), [], CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies, out CXTranslationUnit unit);
        if (error != CXErrorCode.CXError_Success)
        {
            throw new InvalidOperationException($"libclang could not parse the Steam headers: {error}");
        }

        using (unit)
        {
            var sizes = new Dictionary<string, (long Size, long Align)>(StringComparer.Ordinal);
            foreach (CXCursor cursor in unit.Cursor.Children())
            {
                if (cursor.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_ClassDecl && cursor.IsDefinition)
                {
                    string name = cursor.Spelling.ToString();
                    if (_structs.ContainsKey(name))
                    {
                        sizes[name] = (cursor.Type.SizeOf, cursor.Type.AlignOf);
                    }
                }
            }

            return sizes;
        }
    }
}
