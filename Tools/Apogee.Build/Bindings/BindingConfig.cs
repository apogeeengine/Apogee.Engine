using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;

namespace Apogee.Build.Bindings;

/// <summary>How to bind one C library: where its headers are, how names are mapped, which macros group into flags enums.</summary>
public sealed class BindingConfig
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Short id used on the command line (<c>./build.sh bindings sdl</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>Project folder that receives <c>Generated/</c>, relative to the repository root.</summary>
    public string Project { get; set; } = "";

    public string Namespace { get; set; } = "";

    /// <summary>"c" (headers, the default), "vulkan" (the Khronos registry, <see cref="Registry"/>) or "steam" (Valve's <c>steam_api.json</c> and flat header, <see cref="SteamApi"/>).</summary>
    public string Kind { get; set; } = "c";

    /// <summary>For Vulkan: the registry file, relative to the repository root.</summary>
    public string Registry { get; set; } = "";

    /// <summary>For Steam: Valve's <c>steam_api.json</c>, relative to the repository root.</summary>
    public string SteamApi { get; set; } = "";

    /// <summary>For Steam: the flat API's header (<c>steam_api_flat.h</c>), whose declarations are the functions; relative to the repository root.</summary>
    public string FlatHeader { get; set; } = "";

    /// <summary>For Steam: the interfaces (<c>ISteamUser</c>) whose methods are bound.</summary>
    public string[] Interfaces { get; set; } = [];

    /// <summary>For Steam: regexes on the names of the flat functions that are not interface methods (<c>SteamAPI_InitFlat</c>) and are bound as well.</summary>
    public string[] IncludeFunctions { get; set; } = [];

    /// <summary>The static class that holds the functions and constants.</summary>
    public string ClassName { get; set; } = "";

    public LibraryConfig Library { get; set; } = new();

    /// <summary>Include directories, relative to the repository root.</summary>
    public string[] IncludeDirs { get; set; } = [];

    /// <summary>Headers to include, as written in an <c>#include &lt;…&gt;</c> line.</summary>
    public string[] Includes { get; set; } = [];

    /// <summary>Declarations found in files under these directories (relative to the repository root) are bound; the rest is foreign.</summary>
    public string[] OwnDirs { get; set; } = [];

    public string[] Defines { get; set; } = [];

    /// <summary>Prefixes removed from function, type and constant names (the first that matches).</summary>
    public string[] StripPrefixes { get; set; } = [];

    /// <summary>C typedef name → C# type, for typedefs that must not collapse to their underlying type.</summary>
    public Dictionary<string, string> TypeMap { get; set; } = [];

    /// <summary>
    /// Regex on a C type name → C# namespace of the binding assembly that already defines it under the same name (VMA's
    /// <c>VkDevice</c> is Apogee.Native.Vulkan's). Such types are referenced, never generated; a typedef that the other binding
    /// does not have under its C name (<c>VkBufferUsageFlags</c>) goes in <see cref="TypeMap"/>.
    /// </summary>
    public Dictionary<string, string> ForeignTypes { get; set; } = [];

    /// <summary>Suffixes removed from names after the prefix (<c>_t</c> on Flecs types).</summary>
    public string[] StripSuffixes { get; set; } = [];

    /// <summary>C name → C# name, applied to functions, types and constants after prefix stripping.</summary>
    public Dictionary<string, string> Renames { get; set; } = [];

    public FlagsEnumConfig[] FlagsEnums { get; set; } = [];

    /// <summary>Regexes matched against C function names to leave out.</summary>
    public string[] ExcludeFunctions { get; set; } = [];

    /// <summary>Regexes matched against the names of exported variables to leave out (not exported on every platform, or internal).</summary>
    public string[] ExcludeGlobals { get; set; } = [];

    /// <summary>
    /// Regex → replacement, tried in order on a variable's C name before the usual renaming; the first that matches wins.
    /// Flecs' <c>ecs_id(EcsComponent)</c> is the symbol <c>FLECS_IDEcsComponentID_</c>, which <c>^FLECS_ID(.+)ID_$</c> → <c>$1_id</c> turns into <c>ComponentId</c>.
    /// </summary>
    public Dictionary<string, string> GlobalRenames { get; set; } = [];

    /// <summary>Regexes matched against C macro names that must not become constants.</summary>
    public string[] ExcludeConstants { get; set; } = [];

    public static BindingConfig Load(string path)
    {
        BindingConfig config = JsonSerializer.Deserialize<BindingConfig>(File.ReadAllText(path), s_json)
            ?? throw new InvalidDataException($"{path} is empty.");
        if (config.Name.Length == 0 || config.Project.Length == 0 || config.Namespace.Length == 0 || config.ClassName.Length == 0)
        {
            throw new InvalidDataException($"{path}: name, project, namespace and className are required.");
        }

        return config;
    }
}

/// <summary>The file names to try for a native library, per operating system.</summary>
public sealed class LibraryConfig
{
    /// <summary>The logical name used in the <c>DllImport</c>s.</summary>
    public string Name { get; set; } = "";

    public string[] Linux { get; set; } = [];

    public string[] Windows { get; set; } = [];

    public string[] Macos { get; set; } = [];
}

/// <summary>A group of macros (<c>SDL_WINDOW_*</c>) that is bound as a <c>[Flags]</c> enum, and the typedef it replaces.</summary>
public sealed class FlagsEnumConfig
{
    public string Name { get; set; } = "";

    /// <summary>The C typedef (<c>SDL_WindowFlags</c>) whose uses become this enum.</summary>
    public string Typedef { get; set; } = "";

    public string Underlying { get; set; } = "uint";

    public string MacroPrefix { get; set; } = "";

    /// <summary>Regexes on macro names that share the prefix but are not flags; they stay plain constants.</summary>
    public string[] Exclude { get; set; } = [];
}
