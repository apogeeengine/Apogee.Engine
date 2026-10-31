using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ClangSharp.Interop;

namespace Apogee.Build.Bindings;

/// <summary>Parses a C library's headers with libclang into an <see cref="ApiModel"/>.</summary>
public sealed class HeaderParser
{
    private const string Cdecl = "unmanaged[Cdecl]";

    private static readonly Dictionary<string, string> s_stdTypedefs = new()
    {
        ["size_t"] = "nuint", ["uintptr_t"] = "nuint", ["ssize_t"] = "nint", ["ptrdiff_t"] = "nint", ["intptr_t"] = "nint",
        ["int8_t"] = "sbyte", ["uint8_t"] = "byte", ["int16_t"] = "short", ["uint16_t"] = "ushort",
        ["int32_t"] = "int", ["uint32_t"] = "uint", ["int64_t"] = "long", ["uint64_t"] = "ulong",
    };

    private readonly BindingConfig _config;
    private readonly string _root;
    private readonly string[] _ownDirs;
    private readonly ApiModel _model = new();
    private readonly Dictionary<CXCursor, string> _anonymousNames = [];
    private readonly Dictionary<string, CXCursor> _records = [];
    private readonly HashSet<string> _builtRecords = [];
    private readonly HashSet<string> _enumNames = [];
    private readonly HashSet<string> _functionNames = [];
    private readonly HashSet<string> _globalNames = [];
    private readonly HashSet<string> _functionCSharpNames = [];
    private readonly Dictionary<string, string> _flagsByTypedef;
    private readonly (Regex Pattern, string Namespace)[] _foreign;
    private readonly Dictionary<CXCursor, string> _preferredNames = [];
    private readonly Dictionary<string, CXCursor> _enumDeclarations = [];
    private readonly Dictionary<string, string> _typeNamesByC = [];
    private readonly Dictionary<string, string> _typeOwners = [];

    public HeaderParser(BindingConfig config, string repositoryRoot)
    {
        _config = config;
        _root = repositoryRoot;
        _ownDirs = config.OwnDirs.Select(d => Path.GetFullPath(Path.Combine(repositoryRoot, d)).TrimEnd('/', '\\') + Path.DirectorySeparatorChar).ToArray();
        _flagsByTypedef = config.FlagsEnums.ToDictionary(f => f.Typedef, f => f.Name);
        _foreign = config.ForeignTypes.Select(f => (new Regex(f.Key, RegexOptions.CultureInvariant), f.Value)).ToArray();
        foreach (FlagsEnumConfig flags in config.FlagsEnums)
        {
            _typeOwners[flags.Name] = "macro group " + flags.MacroPrefix;
        }
    }

    public ApiModel Parse()
    {
        string directory = Path.Combine(_root, "Bin", "bindings");
        Directory.CreateDirectory(directory);
        string umbrella = Path.Combine(directory, _config.Name + ".h");
        File.WriteAllLines(umbrella, _config.Includes.Select(i => $"#include <{i}>"));

        var args = new List<string> { "-x", "c", "-std=c11", "-resource-dir", ClangResourceDirectory() };
        args.AddRange(_config.IncludeDirs.Select(d => "-I" + Path.GetFullPath(Path.Combine(_root, d))));
        args.AddRange(_config.Defines.Select(d => "-D" + d));

        using CXIndex index = CXIndex.Create();
        CXErrorCode error = CXTranslationUnit.TryParse(
            index, umbrella, args.ToArray(), [],
            CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord | CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies,
            out CXTranslationUnit unit);
        if (error != CXErrorCode.CXError_Success)
        {
            throw new InvalidOperationException($"libclang could not parse {umbrella}: {error}");
        }

        using (unit)
        {
            ThrowOnErrors(unit);
            Visit(unit);
        }

        return _model;
    }

    /// <summary>libclang from NuGet ships without its builtin headers (stdarg.h, stddef.h…), so take them from the installed clang.</summary>
    internal static string ClangResourceDirectory()
    {
        if (Environment.GetEnvironmentVariable("APOGEE_CLANG_RESOURCE_DIR") is { Length: > 0 } configured)
        {
            return configured;
        }

        using Process process = Process.Start(new ProcessStartInfo("clang", "-print-resource-dir") { RedirectStandardOutput = true })
            ?? throw new InvalidOperationException("Could not start clang.");
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && output.Length > 0
            ? output
            : throw new InvalidOperationException("clang -print-resource-dir failed; install clang or set APOGEE_CLANG_RESOURCE_DIR.");
    }

    private static void ThrowOnErrors(CXTranslationUnit unit)
    {
        var errors = new List<string>();
        for (uint i = 0; i < unit.NumDiagnostics; i++)
        {
            using CXDiagnostic diagnostic = unit.GetDiagnostic(i);
            if (diagnostic.Severity >= CXDiagnosticSeverity.CXDiagnostic_Error)
            {
                errors.Add(diagnostic.Format(CXDiagnosticDisplayOptions.CXDiagnostic_DisplaySourceLocation).ToString());
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("libclang reported errors:\n" + string.Join('\n', errors.Take(10)));
        }
    }

    private void Visit(CXTranslationUnit unit)
    {
        var top = unit.Cursor.Children();

        // typedef struct { … } Name;: the typedef gives the unnamed type its name.
        foreach (CXCursor cursor in top.Where(c => c.Kind == CXCursorKind.CXCursor_TypedefDecl))
        {
            CXCursor declaration = cursor.TypedefDeclUnderlyingType.Decl();
            if (declaration.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl or CXCursorKind.CXCursor_EnumDecl
                && IsUnnamed(declaration))
            {
                _anonymousNames[declaration] = cursor.Spelling.ToString();
            }
        }

        // typedef struct X_c X: the typedef is the name people use. And typedef int FooFlags next to enum FooFlags_.
        foreach (CXCursor cursor in top.Where(c => c.Kind == CXCursorKind.CXCursor_TypedefDecl && IsOwn(c)))
        {
            CXCursor declaration = cursor.TypedefDeclUnderlyingType.Decl();
            if (declaration.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl or CXCursorKind.CXCursor_EnumDecl
                && !IsUnnamed(declaration) && declaration.Spelling.ToString() != cursor.Spelling.ToString())
            {
                _preferredNames.TryAdd(declaration, cursor.Spelling.ToString());
            }
        }

        foreach (CXCursor cursor in top.Where(c => c.Kind == CXCursorKind.CXCursor_EnumDecl && c.IsDefinition && IsOwn(c)))
        {
            _enumDeclarations[DeclarationName(cursor, throwIfUnnamed: false)] = cursor;
        }

        var macros = new Dictionary<string, List<string>>();
        var unnamedEnums = new List<CXCursor>();
        var globals = new List<CXCursor>();
        foreach (CXCursor cursor in top)
        {
            if (!IsOwn(cursor))
            {
                continue;
            }

            switch (cursor.Kind)
            {
                case CXCursorKind.CXCursor_MacroDefinition when !cursor.IsMacroFunctionLike && !cursor.IsMacroBuiltIn:
                    CollectMacro(unit, cursor, macros);
                    break;
                case CXCursorKind.CXCursor_FunctionDecl:
                    AddFunction(cursor);
                    break;
                case CXCursorKind.CXCursor_VarDecl when cursor.StorageClass == CX_StorageClass.CX_SC_Extern:
                    globals.Add(cursor);
                    break;
                case CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl when cursor.IsDefinition && !IsUnnamed(cursor):
                    if (Foreign(cursor.Spelling.ToString()) is null)
                    {
                        TryRequire(() => RequireRecord(cursor)); // a type another binding defines (Studio's view of Core's) is not bound again
                    }

                    break;
                case CXCursorKind.CXCursor_EnumDecl when cursor.IsDefinition:
                    if (IsUnnamed(cursor))
                    {
                        unnamedEnums.Add(cursor);
                    }
                    else if (Foreign(cursor.Spelling.ToString()) is null)
                    {
                        TryRequire(() => RequireEnum(cursor));
                    }

                    break;
            }
        }

        // After the functions: a variable and a function share the class's member names (the tag EcsDelete and ecs_delete), and the functions keep theirs.
        foreach (CXCursor cursor in globals)
        {
            AddGlobal(cursor);
        }

        BuildRecords();
        AddConstants(macros, unnamedEnums);
    }

    private void TryRequire(Func<string> require)
    {
        try
        {
            require();
        }
        catch (UnsupportedException e)
        {
            _model.Skipped.Add(e.Message);
        }
    }

    // ---- Declarations ------------------------------------------------------------------------------------------

    private void AddFunction(CXCursor cursor)
    {
        string cName = cursor.Spelling.ToString();
        if (cursor.StorageClass == CX_StorageClass.CX_SC_Static
            || !_functionNames.Add(cName)
            || _config.ExcludeFunctions.Any(p => Regex.IsMatch(cName, p)))
        {
            return;
        }

        if (cursor.Variadic())
        {
            _model.Skipped.Add($"function {cName}: variadic");
            return;
        }

        try
        {
            var function = new FunctionModel
            {
                CName = cName,
                Name = FunctionName(cName),
                ReturnType = MapType(cursor.ResultType),
                Doc = Doc(cursor),
            };
            for (int i = 0; i < cursor.ArgumentCount(); i++)
            {
                CXCursor argument = cursor.Argument(i);
                string name = argument.Spelling.ToString();
                function.Parameters.Add(new ParameterModel(MapParameterType(argument.Type), name.Length == 0 ? $"arg{i}" : name));
            }

            _model.Functions.Add(function);
        }
        catch (UnsupportedException e)
        {
            _model.Skipped.Add($"function {cName}: {e.Message}");
        }
    }

    /// <summary>An exported variable. Its address is looked up once, by name, when first used (see <see cref="CSharpEmitter"/>).</summary>
    private void AddGlobal(CXCursor cursor)
    {
        string cName = cursor.Spelling.ToString();
        if (!_globalNames.Add(cName) || _config.ExcludeGlobals.Any(p => Regex.IsMatch(cName, p)))
        {
            return;
        }

        try
        {
            if (cursor.Type.kind is CXTypeKind.CXType_ConstantArray or CXTypeKind.CXType_IncompleteArray)
            {
                throw new UnsupportedException("arrays are not supported");
            }

            string renamed = cName;
            foreach ((string pattern, string replacement) in _config.GlobalRenames)
            {
                if (Regex.IsMatch(cName, pattern))
                {
                    renamed = Regex.Replace(cName, pattern, replacement);
                    break;
                }
            }

            _model.Globals.Add(new GlobalModel(cName, FunctionName(renamed, "variable"), MapType(cursor.Type), cursor.Type.IsConstQualified, Doc(cursor)));
        }
        catch (UnsupportedException e)
        {
            _model.Skipped.Add($"variable {cName}: {e.Message}");
        }
    }

    private string RequireRecord(CXCursor declaration)
    {
        string name = DeclarationName(declaration);
        if (!IsOwn(declaration))
        {
            throw new UnsupportedException($"type {name} is not from the library's headers");
        }

        string csName = TypeName(name);
        CXCursor definition = declaration.Defn();
        if (!_records.TryGetValue(csName, out CXCursor existing) || (existing.IsDefinition == false && definition.IsDefinition))
        {
            _records[csName] = definition.IsDefinition ? definition : declaration;
        }

        return csName;
    }

    /// <summary>Builds every record that was required, including the ones that building the first ones requires.</summary>
    private void BuildRecords()
    {
        while (_records.Keys.FirstOrDefault(k => !_builtRecords.Contains(k)) is { } name)
        {
            _builtRecords.Add(name);
            CXCursor declaration = _records[name];
            bool isUnion = declaration.Kind == CXCursorKind.CXCursor_UnionDecl;
            var model = new StructModel
            {
                Name = name,
                HasOverlap = isUnion,
                Size = declaration.IsDefinition ? declaration.Type.SizeOf : -1,
                Doc = Doc(declaration),
            };

            if (declaration.IsDefinition)
            {
                try
                {
                    AddFields(model, declaration, 0, name);
                }
                catch (UnsupportedException e)
                {
                    // Keep the type with the right size and alignment, as raw storage.
                    model.Fields.Clear();
                    model.HasOverlap = false;
                    long alignment = Math.Max(1, declaration.Type.AlignOf);
                    string element = alignment >= 8 ? "ulong" : alignment >= 4 ? "uint" : alignment >= 2 ? "ushort" : "byte";
                    int bytes = (int)(alignment >= 8 ? 8 : alignment >= 4 ? 4 : alignment >= 2 ? 2 : 1);
                    model.Fields.Add(new FieldModel("_storage", element, 0, (int)(model.Size / bytes)));
                    _model.Skipped.Add($"struct {name}: {e.Message} (emitted as raw storage)");
                }
            }

            _model.Structs.Add(model);
        }

        _model.Structs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    private void AddFields(StructModel model, CXCursor record, long baseOffset, string typeName)
    {
        foreach (CXCursor field in record.Type.Fields())
        {
            string name = field.Spelling.ToString();
            if (field.IsBitField)
            {
                throw new UnsupportedException($"bit field {name}");
            }

            long offset = baseOffset + (field.OffsetOfField / 8);
            CXType type = field.Type;
            if (IsUnnamed(field))
            {
                // C11 anonymous struct or union member: its fields belong to the parent.
                CXCursor inner = type.Decl();
                model.HasOverlap |= inner.Kind == CXCursorKind.CXCursor_UnionDecl;
                AddFields(model, inner, offset, typeName);
                continue;
            }

            string csName = Naming.Member(Naming.Pascal(name));
            if (csName == typeName || csName is "Equals" or "GetHashCode" or "ToString" or "GetType" or "Finalize" or "MemberwiseClone")
            {
                csName += "_";
            }

            NameNestedType(type, typeName + "_" + Naming.Pascal(name));
            (string csType, int fixedLength) = MapFieldType(type);
            model.Fields.Add(new FieldModel(csName, csType, offset, fixedLength));
        }
    }

    /// <summary>A field whose type is an unnamed struct or union (<c>struct { … } input;</c>) gets the name <c>Parent_Field</c>.</summary>
    private void NameNestedType(CXType fieldType, string name)
    {
        CXType type = Unwrap(fieldType, stripTypedef: false);
        while (type.kind == CXTypeKind.CXType_ConstantArray)
        {
            type = Unwrap(type.Element(), stripTypedef: false);
        }

        if (type.kind == CXTypeKind.CXType_Record && type.Decl() is var declaration && IsUnnamed(declaration) && !_anonymousNames.ContainsKey(declaration))
        {
            _anonymousNames[declaration] = name;
        }
    }

    /// <summary>
    /// A parameter written as an array (<c>float color[4]</c>) is a pointer to the first element in C: libclang reports the written type, but the
    /// function takes the address, so it must not be passed as an inline array by value.
    /// </summary>
    private string MapParameterType(CXType type)
    {
        CXType unwrapped = Unwrap(type, stripTypedef: false);
        return unwrapped.kind is CXTypeKind.CXType_ConstantArray or CXTypeKind.CXType_IncompleteArray
            ? MapType(unwrapped.Element()) + "*"
            : MapType(type);
    }

    private (string Type, int FixedLength) MapFieldType(CXType type)
    {
        CXType unwrapped = Unwrap(type, stripTypedef: false);
        if (unwrapped.kind == CXTypeKind.CXType_ConstantArray)
        {
            // Primitive arrays become `fixed` buffers; (nested) arrays of anything else become inline array structs.
            int length = (int)unwrapped.NumElements;
            CXType element = unwrapped.Element();
            while (Unwrap(element, stripTypedef: false) is { kind: CXTypeKind.CXType_ConstantArray } inner)
            {
                length *= (int)inner.NumElements;
                element = inner.Element();
            }

            string elementType = MapType(element);
            if (elementType is "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double")
            {
                return (elementType, length);
            }
        }

        return (MapType(type), 0);
    }

    private string RequireEnum(CXCursor declaration)
    {
        string name = DeclarationName(declaration);
        if (!IsOwn(declaration))
        {
            throw new UnsupportedException($"type {name} is not from the library's headers");
        }

        string csName = TypeName(name);
        if (!_enumNames.Add(csName))
        {
            return csName;
        }

        string underlying = MapType(declaration.EnumDecl_IntegerType);
        bool unsigned = underlying is "uint" or "ulong" or "ushort" or "byte";
        var model = new EnumModel { Name = csName, Underlying = underlying, IsFlags = csName.EndsWith("Flags", StringComparison.Ordinal) || csName.EndsWith("FlagBits", StringComparison.Ordinal), Doc = Doc(declaration) };

        // The Vulkan-style sentinel (VMA_ALLOCATION_CREATE_FLAG_BITS_MAX_ENUM) only forces the enum to 32 bits; the enum has its own underlying type. So do the
        // members that start with an underscore (C libraries' _X_Count and _X_Force32 sentinels): reserved names are not values.
        var members = declaration.Children()
            .Where(c => c.Kind == CXCursorKind.CXCursor_EnumConstantDecl && !c.Spelling.ToString().EndsWith("_MAX_ENUM", StringComparison.Ordinal) && !c.Spelling.ToString().StartsWith('_'))
            .ToList();
        string[] cNames = members.Select(m => Naming.StripPrefix(m.Spelling.ToString(), _config.StripPrefixes)).ToArray();
        string prefix = CommonWordPrefix(cNames);
        var used = new HashSet<string>();
        for (int i = 0; i < members.Count; i++)
        {
            string bare = cNames[i][prefix.Length..];
            if (model.IsFlags && bare.EndsWith("_BIT", StringComparison.Ordinal) && bare.Length > "_BIT".Length)
            {
                bare = bare[..^"_BIT".Length];
            }

            string memberName = Naming.Member(Naming.Pascal(bare));
            while (!used.Add(memberName))
            {
                memberName += "_";
            }

            string value = unsigned
                ? members[i].EnumConstantDeclUnsignedValue.ToString(CultureInfo.InvariantCulture)
                : members[i].EnumConstantDeclValue.ToString(CultureInfo.InvariantCulture);
            model.Members.Add(new EnumMemberModel(memberName, value));
        }

        _model.Enums.Add(model);
        return csName;
    }

    /// <summary>The longest prefix shared by all names that ends at an underscore, so members lose the enum's own name.</summary>
    private static string CommonWordPrefix(string[] names)
    {
        if (names.Length < 2)
        {
            return "";
        }

        string prefix = names[0];
        foreach (string name in names)
        {
            int length = 0;
            while (length < prefix.Length && length < name.Length && prefix[length] == name[length])
            {
                length++;
            }

            prefix = prefix[..length];
        }

        int cut = prefix.LastIndexOf('_');
        if (cut < 0)
        {
            return "";
        }

        // Never strip a name down to nothing, or to something that starts with a digit only because of the strip.
        string result = prefix[..(cut + 1)];
        return names.Any(n => n.Length == result.Length) ? "" : result;
    }

    // ---- Types -------------------------------------------------------------------------------------------------

    private string MapType(CXType type)
    {
        switch (type.kind)
        {
            case CXTypeKind.CXType_Elaborated:
                return MapType(type.NamedType);
            case CXTypeKind.CXType_Attributed:
                return MapType(type.Modified());
            case CXTypeKind.CXType_Unexposed:
                return MapType(type.CanonicalType);
            case CXTypeKind.CXType_Typedef:
            {
                string name = type.Decl().Spelling.ToString();
                if (_flagsByTypedef.TryGetValue(name, out string? flags))
                {
                    return flags;
                }

                if (_config.TypeMap.TryGetValue(name, out string? mapped) || s_stdTypedefs.TryGetValue(name, out mapped))
                {
                    return mapped;
                }

                // VkFlags-style: VmaAllocatorCreateFlags is the bits enum VmaAllocatorCreateFlagBits.
                if (name.EndsWith("Flags", StringComparison.Ordinal) && _enumDeclarations.TryGetValue(name[..^"Flags".Length] + "FlagBits", out CXCursor flagBits))
                {
                    return RequireEnum(flagBits);
                }

                if (Foreign(name) is { } foreignTypedef)
                {
                    return foreignTypedef;
                }

                if (_enumDeclarations.TryGetValue(name + "_", out CXCursor siblingEnum))
                {
                    return RequireEnum(siblingEnum);
                }

                return MapType(type.Decl().TypedefDeclUnderlyingType);
            }

            case CXTypeKind.CXType_Record:
                return Foreign(type.Decl().Spelling.ToString()) ?? RequireRecord(type.Decl());
            case CXTypeKind.CXType_Enum:
                return Foreign(type.Decl().Spelling.ToString()) ?? RequireEnum(type.Decl());
            case CXTypeKind.CXType_Pointer:
                return MapPointer(type.Pointee());
            case CXTypeKind.CXType_IncompleteArray:
                return MapType(type.Element()) + "*";
            case CXTypeKind.CXType_ConstantArray:
                return InlineArray(MapType(type.Element()), (int)type.NumElements);
            case CXTypeKind.CXType_Void: return "void";
            case CXTypeKind.CXType_Bool: return "CBool";
            case CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U or CXTypeKind.CXType_UChar: return "byte";
            case CXTypeKind.CXType_SChar: return "sbyte";
            case CXTypeKind.CXType_Short: return "short";
            case CXTypeKind.CXType_UShort: return "ushort";
            case CXTypeKind.CXType_Int: return "int";
            case CXTypeKind.CXType_UInt: return "uint";
            case CXTypeKind.CXType_Long: return "CLong";
            case CXTypeKind.CXType_ULong: return "CULong";
            case CXTypeKind.CXType_LongLong: return "long";
            case CXTypeKind.CXType_ULongLong: return "ulong";
            case CXTypeKind.CXType_Float: return "float";
            case CXTypeKind.CXType_Double: return "double";
            default:
                throw new UnsupportedException($"type {type.Spelling} ({type.kind})");
        }
    }

    private string MapPointer(CXType pointee)
    {
        CXType probe = Unwrap(pointee, stripTypedef: true);
        switch (probe.kind)
        {
            case CXTypeKind.CXType_FunctionProto:
            {
                if (probe.IsFunctionTypeVariadic)
                {
                    throw new UnsupportedException("variadic function pointer");
                }

                var parts = new List<string>();
                for (uint i = 0; i < (uint)probe.NumArgTypes; i++)
                {
                    parts.Add(MapType(probe.GetArgType(i)));
                }

                parts.Add(MapType(probe.ResultType));
                return $"delegate* {Cdecl}<{string.Join(", ", parts)}>";
            }

            case CXTypeKind.CXType_FunctionNoProto:
                return "void*";
            case CXTypeKind.CXType_Void or CXTypeKind.CXType_WChar:
                return "void*";
            case CXTypeKind.CXType_Record or CXTypeKind.CXType_Enum when !IsOwn(probe.Decl()) && Foreign(probe.Decl().Spelling.ToString()) is null:
                return "void*";
            default:
                return MapType(pointee) + "*";
        }
    }

    /// <summary>The C# name of a type that another binding assembly defines (<see cref="BindingConfig.ForeignTypes"/>), or null.</summary>
    private string? Foreign(string cName)
    {
        foreach ((Regex pattern, string ns) in _foreign)
        {
            if (pattern.IsMatch(cName))
            {
                _model.Usings.Add(ns);
                return TypeName(cName); // named the way that binding names it (the same prefixes and renames in both configs)
            }
        }

        return null;
    }

    private static CXType Unwrap(CXType type, bool stripTypedef)
    {
        while (true)
        {
            switch (type.kind)
            {
                case CXTypeKind.CXType_Elaborated:
                    type = type.NamedType;
                    break;
                case CXTypeKind.CXType_Attributed:
                    type = type.Modified();
                    break;
                case CXTypeKind.CXType_Typedef when stripTypedef:
                    type = type.Decl().TypedefDeclUnderlyingType;
                    break;
                default:
                    return type;
            }
        }
    }

    private string InlineArray(string elementType, int length)
    {
        string name = $"InlineArray{length}_{Sanitize(elementType)}";
        if (!_model.InlineArrays.ContainsKey(name))
        {
            // An inline array's element cannot be a pointer type: store pointers as pointer-sized integers.
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

    // ---- Names and locations -----------------------------------------------------------------------------------

    /// <summary>
    /// The C# name of a type. Stripping prefixes can make two C types share a name (<c>ecs_member_t</c> and <c>EcsMember</c>);
    /// the later one then keeps its full C name, and the report says so.
    /// </summary>
    private string TypeName(string cName)
    {
        if (_typeNamesByC.TryGetValue(cName, out string? known))
        {
            return known;
        }

        string name = Rename(cName);
        if (_typeOwners.TryGetValue(name, out string? owner) && owner != cName)
        {
            string full = cName;
            foreach (string suffix in _config.StripSuffixes)
            {
                if (full.EndsWith(suffix, StringComparison.Ordinal))
                {
                    full = full[..^suffix.Length];
                    break;
                }
            }

            full = Naming.Pascal(full);
            while (_typeOwners.ContainsKey(full))
            {
                full += "_";
            }

            _model.Skipped.Add($"type {cName}: the name {name} belongs to {owner}, so it is named {full}");
            name = full;
        }

        _typeOwners[name] = cName;
        _typeNamesByC[cName] = name;
        return name;
    }

    /// <summary>The C# name of a function; a clash with an earlier one (<c>ImDrawList_AddLine</c>, <c>igImDrawListAddLine</c>) keeps the full C name.</summary>
    private string FunctionName(string cName, string kind = "function")
    {
        string name = Rename(cName);
        if (!_functionCSharpNames.Add(name))
        {
            name = Naming.Capitalize(cName);
            while (!_functionCSharpNames.Add(name))
            {
                name += "_";
            }

            _model.Skipped.Add($"{kind} {cName}: the name is taken, so it is named {name}");
        }

        return name;
    }

    private string Rename(string cName)
    {
        if (_config.Renames.TryGetValue(cName, out string? renamed))
        {
            return renamed;
        }

        string name = Naming.StripPrefix(cName, _config.StripPrefixes);
        foreach (string suffix in _config.StripSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return name.Contains('_', StringComparison.Ordinal) ? Naming.Pascal(name) : Naming.Capitalize(name);
    }

    private string DeclarationName(CXCursor declaration, bool throwIfUnnamed = true)
    {
        if (_anonymousNames.TryGetValue(declaration, out string? name) || _preferredNames.TryGetValue(declaration, out name))
        {
            return name;
        }

        if (IsUnnamed(declaration))
        {
            return throwIfUnnamed ? throw new UnsupportedException("unnamed type") : "";
        }

        return declaration.Spelling.ToString();
    }

    private static bool IsUnnamed(CXCursor declaration)
    {
        string spelling = declaration.Spelling.ToString();
        return spelling.Length == 0 || spelling.Contains("(unnamed", StringComparison.Ordinal) || spelling.Contains("(anonymous", StringComparison.Ordinal);
    }

    private bool IsOwn(CXCursor cursor)
    {
        cursor.Location.GetFileLocation(out CXFile file, out _, out _, out _);
        string path = file.Name.ToString();
        return path.Length > 0 && _ownDirs.Any(d => Path.GetFullPath(path).StartsWith(d, StringComparison.Ordinal));
    }

    private static string? Doc(CXCursor cursor)
    {
        string brief = cursor.BriefCommentText.ToString();

        // FreeType's headers open a comment with a row of asterisks: a line of punctuation is not a description.
        if (!brief.Any(char.IsLetterOrDigit))
        {
            return null;
        }

        string flat = Regex.Replace(brief, @"\s+", " ").Trim();
        return flat.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    }

    // ---- Constants ---------------------------------------------------------------------------------------------

    private static void CollectMacro(CXTranslationUnit unit, CXCursor cursor, Dictionary<string, List<string>> macros)
    {
        var tokens = unit.Tokenize(cursor.Extent).ToArray().Select(t => t.GetSpelling(unit).ToString()).ToList();
        if (tokens.Count > 0)
        {
            string name = tokens[0];
            tokens.RemoveAt(0);
            macros[name] = tokens;
        }
    }

    private void AddConstants(Dictionary<string, List<string>> macros, List<CXCursor> unnamedEnums)
    {
        var evaluator = new MacroEvaluator(macros);
        var claimed = new HashSet<string>();

        foreach (FlagsEnumConfig flags in _config.FlagsEnums)
        {
            var model = new EnumModel { Name = flags.Name, Underlying = flags.Underlying, IsFlags = true };
            foreach (string macro in macros.Keys.Where(m => m.StartsWith(flags.MacroPrefix, StringComparison.Ordinal) && !flags.Exclude.Any(p => Regex.IsMatch(m, p))).Order(StringComparer.Ordinal))
            {
                if (evaluator.EvaluateInteger(macro) is { } value)
                {
                    model.Members.Add(new EnumMemberModel(Naming.Member(Naming.Pascal(macro[flags.MacroPrefix.Length..])), value.ToString(CultureInfo.InvariantCulture)));
                    claimed.Add(macro);
                }
            }

            _model.Enums.Add(model);
        }

        var used = new HashSet<string>(_model.Functions.Select(f => f.Name));
        foreach (string macro in macros.Keys.Order(StringComparer.Ordinal))
        {
            if (claimed.Contains(macro) || macro.StartsWith('_') || _config.ExcludeConstants.Any(p => Regex.IsMatch(macro, p)))
            {
                continue;
            }

            string name = Naming.Member(Naming.Pascal(Naming.StripPrefix(macro, _config.StripPrefixes)));
            while (!used.Add(name))
            {
                name += "_";
            }

            if (evaluator.EvaluateInteger(macro) is { } integer)
            {
                string type = integer >= int.MinValue && integer <= int.MaxValue ? "int"
                    : integer >= long.MinValue && integer <= long.MaxValue ? "long"
                    : integer <= ulong.MaxValue && integer >= 0 ? "ulong" : "";
                if (type.Length > 0)
                {
                    _model.Constants.Add(new ConstantModel(name, type, integer.ToString(CultureInfo.InvariantCulture)));
                }
            }
            else if (evaluator.EvaluateString(macro) is { } text)
            {
                _model.Constants.Add(new ConstantModel(name, "string", text));
            }
        }

        foreach (CXCursor cursor in unnamedEnums)
        {
            foreach (CXCursor member in cursor.Children().Where(c => c.Kind == CXCursorKind.CXCursor_EnumConstantDecl))
            {
                string name = Naming.Member(Naming.Pascal(Naming.StripPrefix(member.Spelling.ToString(), _config.StripPrefixes)));
                if (used.Add(name))
                {
                    _model.Constants.Add(new ConstantModel(name, "int", member.EnumConstantDeclValue.ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        _model.Constants.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _model.Enums.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _model.Functions.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    private sealed class UnsupportedException(string message) : Exception(message);
}
