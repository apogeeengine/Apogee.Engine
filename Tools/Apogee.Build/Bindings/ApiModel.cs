using System.Collections.Generic;

namespace Apogee.Build.Bindings;

/// <summary>Everything the parser found in a library's headers, with C# type strings already resolved.</summary>
public sealed class ApiModel
{
    public List<FunctionModel> Functions { get; } = [];

    public List<EnumModel> Enums { get; } = [];

    public List<StructModel> Structs { get; } = [];

    public List<ConstantModel> Constants { get; } = [];

    /// <summary>Exported variables (<c>extern const ecs_entity_t EcsOnUpdate;</c>), read from the library's data segment.</summary>
    public List<GlobalModel> Globals { get; } = [];

    /// <summary>Opaque handle types (Vulkan): a struct that wraps the pointer or 64-bit id.</summary>
    public List<HandleModel> Handles { get; } = [];

    /// <summary>Commands that are loaded through a function pointer, not imported (Vulkan).</summary>
    public List<CommandModel> Commands { get; } = [];

    /// <summary>Inline array structs the fields refer to, keyed by name.</summary>
    public SortedDictionary<string, InlineArrayModel> InlineArrays { get; } = [];

    /// <summary>Namespaces of other binding assemblies whose types the declarations use (<see cref="BindingConfig.ForeignTypes"/>).</summary>
    public SortedSet<string> Usings { get; } = [];

    /// <summary>What was left out, and why, for the report.</summary>
    public List<string> Skipped { get; } = [];
}

public sealed record ParameterModel(string Type, string Name);

public sealed class FunctionModel
{
    public required string CName { get; init; }

    public required string Name { get; init; }

    public required string ReturnType { get; init; }

    public List<ParameterModel> Parameters { get; } = [];

    public string? Doc { get; init; }
}

public sealed class EnumModel
{
    public required string Name { get; init; }

    public required string Underlying { get; init; }

    public bool IsFlags { get; init; }

    public List<EnumMemberModel> Members { get; } = [];

    public string? Doc { get; init; }
}

public sealed record EnumMemberModel(string Name, string Value);

public sealed class StructModel
{
    public required string Name { get; init; }

    /// <summary>True for unions and structs with an anonymous union: fields then carry explicit offsets.</summary>
    public bool HasOverlap { get; set; }

    /// <summary>Size clang computed on the host (a 64-bit target), used by the layout test; -1 when incomplete.</summary>
    public long Size { get; set; }

    /// <summary>Steam: the struct's layout depends on the platform's callback packing (4 on Linux and macOS, 8 on Windows); it is declared with <c>Pack = SteamPack.Callback</c>.</summary>
    public bool PackDependent { get; set; }

    /// <summary>Steam: the struct is declared inside a <c>#pragma pack</c> on every platform (4, or 2 for a few), so it says <c>Pack = n</c>: a plain C# struct would align its 8-byte members to 8. 0 when not packed.</summary>
    public int FixedPack { get; set; }

    /// <summary>Steam: the size where the packing is 8 (Windows), when it differs from <see cref="Size"/>.</summary>
    public long LargePackSize { get; set; }

    /// <summary>For a Vulkan struct that starts with <c>sType</c>: the <c>VkStructureType</c> member that identifies it, so that <c>new T()</c> sets it.</summary>
    public string? StructureType { get; set; }

    public List<FieldModel> Fields { get; } = [];

    public string? Doc { get; init; }
}

/// <param name="Offset">Byte offset; only written out for unions.</param>
/// <param name="FixedLength">Length of a <c>fixed</c> buffer, or 0 when the field is not one.</param>
public sealed record FieldModel(string Name, string Type, long Offset, int FixedLength);

public sealed record InlineArrayModel(string Name, string ElementType, int Length);

/// <param name="Type"><c>int</c>, <c>long</c>, <c>ulong</c> or <c>string</c>.</param>
public sealed record ConstantModel(string Name, string Type, string Value);

/// <param name="CName">The exported symbol.</param>
/// <param name="IsConst">True when the C variable is <c>const</c>: the property then returns <c>ref readonly</c>.</param>
public sealed record GlobalModel(string CName, string Name, string Type, bool IsConst, string? Doc);

/// <param name="Dispatchable">True for pointer handles (<c>VK_DEFINE_HANDLE</c>), false for 64-bit ids.</param>
public sealed record HandleModel(string Name, bool Dispatchable);

/// <summary>A Vulkan command and the level it is loaded at.</summary>
public sealed class CommandModel
{
    public required string CName { get; init; }

    public required string Name { get; init; }

    public required string ReturnType { get; init; }

    public List<ParameterModel> Parameters { get; } = [];

    public required CommandLevel Level { get; init; }
}

public enum CommandLevel
{
    /// <summary>Loaded with no instance (<c>vkCreateInstance</c>, <c>vkEnumerateInstance*</c>).</summary>
    Global,

    /// <summary>Takes a <c>VkInstance</c> or <c>VkPhysicalDevice</c>.</summary>
    Instance,

    /// <summary>Takes a <c>VkDevice</c>, <c>VkQueue</c> or <c>VkCommandBuffer</c>.</summary>
    Device,
}
