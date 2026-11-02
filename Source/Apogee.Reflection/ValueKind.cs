namespace Apogee.Reflection;

/// <summary>What a <see cref="Value"/> holds.</summary>
public enum ValueKind : byte
{
    Nil,
    Bool,

    /// <summary>A 64-bit integer. Scripts only have doubles, so the bridge converts by the declared type.</summary>
    Int,
    Float,
    String,
    Vector3,

    /// <summary>A rotation: the <c>Quat</c> userdata in scripts.</summary>
    Quaternion,
    Enum,
    Object,
    Entity,
    Function,
    List,

    /// <summary>A script's table, kept by reference (<see cref="ScriptTable"/>).</summary>
    Table,
}
