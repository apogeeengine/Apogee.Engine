using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Native.Luau;
using Apogee.Reflection;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

public sealed unsafe partial class LuauVm
{
    private const int MaxDepth = 16;

    // ---- Luau → Value, by the declared type ------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the value at <paramref name="index"/> as <paramref name="type"/>: strictly (a number is not a string), with the objects, enums,
    /// functions and lists of the registry. A number stays a float; the integer checks happen when the member converts it.
    /// </summary>
    internal Value ReadValue(State* L, int index, TypeRef type, int depth = 0)
    {
        int luaType = Lua.Type(L, index);
        if (luaType == (int)LuaType.Tnil)
        {
            return type.Nullable || type.IsAny ? default : throw Mismatch(type, "nil");
        }

        switch (type.Kind)
        {
            case ValueKind.Nil:
                return ReadAny(L, index, depth);
            case ValueKind.Bool:
                return luaType == (int)LuaType.Tboolean ? Value.Bool(Lua.Toboolean(L, index) != 0) : throw Mismatch(type, L, index);
            case ValueKind.Int:
            case ValueKind.Float:
                return luaType == (int)LuaType.Tnumber ? Value.Float(Lua.Tonumberx(L, index, null)) : throw Mismatch(type, L, index);
            case ValueKind.String:
                return luaType == (int)LuaType.Tstring ? Value.String(Stack.ReadString(L, index)) : throw Mismatch(type, L, index);
            case ValueKind.Vector3:
                return luaType == (int)LuaType.Tvector ? Stack.ReadValue(L, index) : throw Mismatch(type, L, index);
            case ValueKind.Quaternion:
                return IsQuat(L, index) ? Value.Quat(ReadQuat(L, index)) : throw Mismatch(type, L, index);
            case ValueKind.Entity:
                return Value.Entity(ReadEntity(L, index));
            case ValueKind.Enum:
                return ReadEnum(L, index, type, depth);
            case ValueKind.Object:
                return luaType == (int)LuaType.Tuserdata ? Value.Object(GetObject(L, index)) : throw Mismatch(type, L, index);
            case ValueKind.Function:
                return luaType == (int)LuaType.Tfunction ? Value.Function(CreateFunction(L, index)) : throw Mismatch(type, L, index);
            case ValueKind.Table:
                return luaType == (int)LuaType.Ttable ? Value.Table(CreateTable(L, index)) : throw Mismatch(type, L, index);
            default:
                return ReadList(L, index, type.Element ?? TypeRef.Any, depth);
        }
    }

    private Value ReadAny(State* L, int index, int depth)
    {
        switch ((LuaType)Lua.Type(L, index))
        {
            case LuaType.Tuserdata: return IsQuat(L, index) ? Value.Quat(ReadQuat(L, index)) : Value.Object(GetObject(L, index));
            case LuaType.Tfunction: return Value.Function(CreateFunction(L, index));
            case LuaType.Ttable: return ReadList(L, index, TypeRef.Any, depth);
            default: return Stack.ReadValue(L, index);
        }
    }

    private Value ReadList(State* L, int index, TypeRef element, int depth)
    {
        if (Lua.Type(L, index) != (int)LuaType.Ttable)
        {
            throw Mismatch(TypeRef.List(element), L, index);
        }

        if (depth >= MaxDepth)
        {
            throw new ValueException("the table is nested too deep (is it a cycle?)");
        }

        int absolute = Lua.Absindex(L, index);
        int count = Lua.Objlen(L, absolute);
        var items = new List<Value>(count);
        Stack.Reserve(L, 3);
        for (int i = 1; i <= count; i++)
        {
            _ = Stack.RawGetI(L, absolute, i);
            try
            {
                items.Add(ReadValue(L, -1, element, depth + 1));
            }
            finally
            {
                Lua.Settop(L, -2);
            }
        }

        return Value.List(items);
    }

    /// <summary>An entity is a number; a pair is a table of two numbers (a pair id has bits above the 53 a number holds).</summary>
    internal static Entity ReadEntity(State* L, int index)
    {
        switch ((LuaType)Lua.Type(L, index))
        {
            case LuaType.Tnumber:
                double number = Lua.Tonumberx(L, index, null);
                if (number < 0 || number != Math.Floor(number) || number >= 9007199254740992.0)
                {
                    throw new ValueException($"{Stack.FormatNumber(number)} is not an entity id");
                }

                return new Entity((ulong)number);
            case LuaType.Ttable:
                int absolute = Lua.Absindex(L, index);
                Stack.Reserve(L, 2);
                _ = Stack.RawGetI(L, absolute, 1);
                _ = Stack.RawGetI(L, absolute, 2);
                try
                {
                    if (!Stack.Is(L, -2, LuaType.Tnumber) || !Stack.Is(L, -1, LuaType.Tnumber))
                    {
                        throw new ValueException("a pair is a table of two entity ids");
                    }

                    return new Entity(Apogee.Ecs.World.Pair(new Entity((ulong)Lua.Tonumberx(L, -2, null)), new Entity((ulong)Lua.Tonumberx(L, -1, null))).Id);
                }
                finally
                {
                    Lua.Settop(L, -3);
                }

            default:
                throw new ValueException($"expected an entity id, got {Stack.Describe(L, index)}");
        }
    }

    private Value ReadEnum(State* L, int index, TypeRef type, int depth)
    {
        EnumInfo info = TypeRegistry.GetEnum(type.Name!);
        int luaType = Lua.Type(L, index);
        if (luaType == (int)LuaType.Tstring)
        {
            string name = Stack.ReadString(L, index);
            return info.TryGetValue(name, out long value) ? Value.Enum(info, value) : throw UnknownMember(info, name);
        }

        if (info.IsFlags && luaType == (int)LuaType.Ttable)
        {
            Value list = ReadList(L, index, TypeRef.String, depth);
            long flags = 0;
            foreach (Value item in list.ToList())
            {
                string name = item.ToStringValue();
                flags |= info.TryGetValue(name, out long value) ? value : throw UnknownMember(info, name);
            }

            return Value.Enum(info, flags);
        }

        throw Mismatch(type, L, index);
    }

    private static ValueException UnknownMember(EnumInfo info, string name) =>
        new($"'{name}' is not a {info.Name}; use one of: {string.Join(", ", info.Members.Select(m => m.Name))}");

    private static ValueException Mismatch(TypeRef expected, State* L, int index) => Mismatch(expected, Stack.TypeName(L, index));

    private static ValueException Mismatch(TypeRef expected, string got) => new($"expected {Describe(expected)}, got {got}");

    internal static string Describe(TypeRef type) => type.Kind switch
    {
        ValueKind.Nil => "any value",
        ValueKind.Bool => "boolean",
        ValueKind.Int or ValueKind.Float => "number",
        ValueKind.String => "string",
        ValueKind.Vector3 => "vector",
        ValueKind.Quaternion => "Quat",
        ValueKind.Entity => "entity id",
        ValueKind.Enum => $"{type.Name} (a string)",
        ValueKind.Object => type.Name!,
        ValueKind.Function => "function",
        ValueKind.Table => "table",
        _ => "table",
    };

    // ---- Value → Luau ------------------------------------------------------------------------------------------------------------------

    /// <summary>Pushes the value: numbers as numbers, enums as their names, objects as userdata, lists as tables.</summary>
    internal void PushValue(State* L, in Value value, int depth = 0)
    {
        Stack.Reserve(L, 4);
        switch (value.Kind)
        {
            case ValueKind.Enum:
                EnumInfo info = value.EnumType;
                if (info.IsFlags)
                {
                    Lua.Createtable(L, 0, 0);
                    int n = 0;
                    foreach (EnumMemberInfo member in info.Members)
                    {
                        if (member.Value != 0 && (value.ToEnum() & member.Value) == member.Value)
                        {
                            Stack.PushString(L, member.Name);
                            Lua.Rawseti(L, -2, ++n);
                        }
                    }
                }
                else
                {
                    Stack.PushString(L, info.GetName(value.ToEnum()));
                }

                break;
            case ValueKind.Object:
                PushObject(L, value.ToObject<EngineObject>());
                break;
            case ValueKind.Function:
                if (value.ToFunction() is LuauFunction function && function.Vm == this)
                {
                    _ = Stack.RawGetI(L, Lua.Registryindex, function.Reference);
                }
                else
                {
                    throw new ValueException("only a script's own function can be passed back to scripts");
                }

                break;
            case ValueKind.Table:
                if (value.ToTable() is LuauTable table && table.Vm == this)
                {
                    _ = Stack.RawGetI(L, Lua.Registryindex, table.Reference);
                }
                else
                {
                    throw new ValueException("only a script's own table can be passed back to scripts");
                }

                break;
            case ValueKind.List:
                if (depth >= MaxDepth)
                {
                    throw new ValueException("the list is nested too deep");
                }

                IReadOnlyList<Value> items = value.ToList();
                Lua.Createtable(L, items.Count, 0);
                for (int i = 0; i < items.Count; i++)
                {
                    PushValue(L, items[i], depth + 1);
                    Lua.Rawseti(L, -2, i + 1);
                }

                break;
            default:
                Stack.PushValue(L, value);
                break;
        }
    }
}
