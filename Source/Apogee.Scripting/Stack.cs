using System;
using System.Text;
using Apogee.Native.Luau;
using Apogee.Reflection;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

/// <summary>Reading and pushing values on a Luau stack. Pushing needs a free slot: a native function starts with 20.</summary>
internal static unsafe class Stack
{
    public static int TypeOf(State* L, int index) => Lua.Type(L, index);

    /// <summary>The Luau name of the type at <paramref name="index"/>: <c>number</c>, <c>table</c>, <c>userdata</c>…</summary>
    public static string TypeName(State* L, int index) => Apogee.Interop.Utf8.FromPointer(Lua.Typename(L, Lua.Type(L, index))) ?? "?";

    /// <summary><c>t[n]</c> without metamethods, pushed. Returns the type of what it pushed.</summary>
    public static int RawGetI(State* L, int table, int n) => Lua.Rawgeti(L, table, n);

    public static bool Is(State* L, int index, LuaType type) => Lua.Type(L, index) == (int)type;

    public static string ReadString(State* L, int index)
    {
        nuint length;
        byte* text = Lua.Tolstring(L, index, &length);
        return text is null ? "" : Encoding.UTF8.GetString(text, (int)length);
    }

    public static void PushString(State* L, string text)
    {
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (max <= 512)
        {
            Span<byte> buffer = stackalloc byte[Math.Max(max, 1)];
            int length = Encoding.UTF8.GetBytes(text, buffer);
            fixed (byte* bytes = buffer)
            {
                Lua.Pushlstring(L, bytes, (nuint)length);
            }

            return;
        }

        byte[] encoded = Encoding.UTF8.GetBytes(text);
        fixed (byte* bytes = encoded)
        {
            Lua.Pushlstring(L, bytes, (nuint)encoded.Length);
        }
    }

    /// <summary>Makes sure <paramref name="extra"/> more values fit; false when the stack cannot grow (it never raises an error).</summary>
    public static bool Reserve(State* L, int extra) => Lua.Checkstack(L, extra) != 0;

    public static string Describe(State* L, int index)
    {
        switch ((LuaType)Lua.Type(L, index))
        {
            case LuaType.Tnil: return "nil";
            case LuaType.Tboolean: return Lua.Toboolean(L, index) != 0 ? "true" : "false";
            case LuaType.Tnumber: return FormatNumber(Lua.Tonumberx(L, index, null));
            case LuaType.Tstring: return ReadString(L, index);
            case LuaType.Tvector:
                float* v = Lua.Tovector(L, index);
                return $"{FormatNumber(v[0])}, {FormatNumber(v[1])}, {FormatNumber(v[2])}";
            case LuaType.Tuserdata when LuauVm.IsQuat(L, index):
                return LuauVm.DescribeQuat(LuauVm.ReadQuat(L, index));
            default:
                byte* name = Lua.Typename(L, Lua.Type(L, index));
                return $"{Apogee.Interop.Utf8.FromPointer(name)}: 0x{(nint)Lua.Topointer(L, index):x}";
        }
    }

    public static string FormatNumber(double number) =>
        number == Math.Floor(number) && Math.Abs(number) < 1e15
            ? ((long)number).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    // ---- Values ------------------------------------------------------------------------------------------------------------

    /// <summary>Reads the value at <paramref name="index"/>: nil, boolean, number, string and vector.</summary>
    public static Value ReadValue(State* L, int index)
    {
        switch ((LuaType)Lua.Type(L, index))
        {
            case LuaType.Tnil: return default;
            case LuaType.Tboolean: return Value.Bool(Lua.Toboolean(L, index) != 0);
            case LuaType.Tnumber: return Value.Float(Lua.Tonumberx(L, index, null));
            case LuaType.Tstring: return Value.String(ReadString(L, index));
            case LuaType.Tvector:
                float* v = Lua.Tovector(L, index);
                return Value.Vector(new System.Numerics.Vector3(v[0], v[1], v[2]));
            case LuaType.Tuserdata when LuauVm.IsQuat(L, index):
                return Value.Quat(LuauVm.ReadQuat(L, index));
            default:
                throw new ValueException($"a {Apogee.Interop.Utf8.FromPointer(Lua.Typename(L, Lua.Type(L, index)))} cannot be passed to the engine here");
        }
    }

    public static void PushValue(State* L, in Value value)
    {
        switch (value.Kind)
        {
            case ValueKind.Nil: Lua.Pushnil(L); break;
            case ValueKind.Bool: Lua.Pushboolean(L, value.ToBool() ? 1 : 0); break;
            case ValueKind.Int:
            case ValueKind.Float: Lua.Pushnumber(L, value.ToDouble()); break;
            case ValueKind.String: PushString(L, value.ToStringValue()); break;
            case ValueKind.Vector3:
                System.Numerics.Vector3 vector = value.ToVector3();
                Lua.Pushvector(L, vector.X, vector.Y, vector.Z);
                break;
            case ValueKind.Quaternion: LuauVm.PushQuat(L, value.ToQuaternion()); break;
            case ValueKind.Entity: Lua.Pushnumber(L, value.ToEntity().Id); break;
            default: throw new ValueException($"a {value.Kind} cannot be passed to a script here");
        }
    }
}
