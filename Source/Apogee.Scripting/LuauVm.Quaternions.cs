using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apogee.Native.Luau;
using Apogee.Reflection;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

public sealed unsafe partial class LuauVm
{
    /// <summary>The tag of the <c>Quat</c> userdata: the four floats of a <see cref="Quaternion"/>, immutable.</summary>
    internal const int QuatTag = 5;

    private static readonly string[] s_quatMethods = ["inverse", "normalized", "slerp", "toEuler", "dot"];

    /// <summary>
    /// <c>Quat</c>: rotations for scripts. <c>a * b</c> applies <c>b</c> first, then <c>a</c> (so <c>(a * b) * v == a * (b * v)</c>), and
    /// <c>q * vector</c> rotates the vector. Euler angles are radians as (pitch about x, yaw about y, roll about z); the roll is applied
    /// first, then the pitch, then the yaw. A camera or an object looks down -Z.
    /// </summary>
    private void InstallQuat()
    {
        foreach (string name in s_quatMethods)
        {
            AtomOf(name);
        }

        Lua.Createtable(_main, 0, 8);
        SetMetaFunction("__index", &QuatIndex);
        SetMetaFunction("__namecall", &QuatNameCall);
        SetMetaFunction("__tostring", &QuatToString);
        SetMetaFunction("__mul", &QuatMultiply);
        SetMetaFunction("__eq", &QuatEquals);
        Stack.PushString(_main, "The metatable is locked");
        SetField("__metatable");
        Lua.Setreadonly(_main, -1, 1);
        Lua.Setuserdatametatable(_main, QuatTag);

        Lua.Createtable(_main, 0, 6);
        PushQuat(_main, Quaternion.Identity);
        SetField("identity");
        RegisterQuatFunction("new", &QuatNew);
        RegisterQuatFunction("axisAngle", &QuatAxisAngle);
        RegisterQuatFunction("euler", &QuatEuler);
        RegisterQuatFunction("lookAt", &QuatLookAt);
        Lua.Setreadonly(_main, -1, 1);
        SetMainGlobal("Quat");
    }

    private void RegisterQuatFunction(string name, delegate* unmanaged[Cdecl]<State*, int> function)
    {
        Lua.PushManagedFunction(_main, function, NativeName("Quat." + name), 0);
        SetField(name);
    }

    internal static void PushQuat(State* L, Quaternion value)
    {
        Stack.Reserve(L, 2);
        *(Quaternion*)Lua.Newuserdatataggedwithmetatable(L, (nuint)sizeof(Quaternion), QuatTag) = value;
    }

    internal static bool IsQuat(State* L, int index) => Lua.Type(L, index) == (int)LuaType.Tuserdata && Lua.Userdatatag(L, index) == QuatTag;

    internal static Quaternion ReadQuat(State* L, int index)
    {
        var quat = (Quaternion*)Lua.Touserdatatagged(L, index, QuatTag);
        return quat is null ? throw new ValueException($"expected Quat, got {Stack.TypeName(L, index)}") : *quat;
    }

    internal static string DescribeQuat(Quaternion q) =>
        $"Quat({Stack.FormatNumber(q.X)}, {Stack.FormatNumber(q.Y)}, {Stack.FormatNumber(q.Z)}, {Stack.FormatNumber(q.W)})";

    private static float Number(State* L, int index, string function)
    {
        if (!Stack.Is(L, index, LuaType.Tnumber))
        {
            throw new ValueException($"{function} expects a number for argument {index}, got {Stack.TypeName(L, index)}");
        }

        return (float)Lua.Tonumberx(L, index, null);
    }

    private static Vector3 VectorArgument(State* L, int index, string function)
    {
        if (!Stack.Is(L, index, LuaType.Tvector))
        {
            throw new ValueException($"{function} expects a vector for argument {index}, got {Stack.TypeName(L, index)}");
        }

        float* v = Lua.Tovector(L, index);
        return new Vector3(v[0], v[1], v[2]);
    }

    // ---- Quat.new, axisAngle, euler, lookAt -------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QuatNew(State* L) => Guard(L, &QuatNewBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QuatAxisAngle(State* L) => Guard(L, &QuatAxisAngleBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QuatEuler(State* L) => Guard(L, &QuatEulerBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QuatLookAt(State* L) => Guard(L, &QuatLookAtBody);

    /// <summary>Quat.new(x, y, z, w): the components as they are, so they should make a unit quaternion.</summary>
    private static int QuatNewBody(State* L)
    {
        if (Lua.Gettop(L) != 4)
        {
            throw new ValueException("Quat.new takes x, y, z and w");
        }

        PushQuat(L, new Quaternion(Number(L, 1, "Quat.new"), Number(L, 2, "Quat.new"), Number(L, 3, "Quat.new"), Number(L, 4, "Quat.new")));
        return 1;
    }

    /// <summary>Quat.axisAngle(axis, radians): a turn about the axis, which need not be a unit vector but must not be zero.</summary>
    private static int QuatAxisAngleBody(State* L)
    {
        Vector3 axis = VectorArgument(L, 1, "Quat.axisAngle");
        float angle = Number(L, 2, "Quat.axisAngle");
        float length = axis.Length();
        if (!(length > 1e-12f) || !float.IsFinite(length))
        {
            throw new ValueException("Quat.axisAngle needs an axis that is not zero");
        }

        PushQuat(L, Quaternion.CreateFromAxisAngle(axis / length, angle));
        return 1;
    }

    /// <summary>Quat.euler(vector(pitch, yaw, roll)) in radians.</summary>
    private static int QuatEulerBody(State* L)
    {
        Vector3 angles = VectorArgument(L, 1, "Quat.euler");
        PushQuat(L, Quaternion.CreateFromYawPitchRoll(angles.Y, angles.X, angles.Z));
        return 1;
    }

    /// <summary>Quat.lookAt(forward, up): the rotation that turns -Z to <c>forward</c> with +Y as near to <c>up</c> as it can be.</summary>
    private static int QuatLookAtBody(State* L)
    {
        Vector3 forward = VectorArgument(L, 1, "Quat.lookAt");
        Vector3 up = Lua.Gettop(L) >= 2 ? VectorArgument(L, 2, "Quat.lookAt") : Vector3.UnitY;
        if (!(forward.LengthSquared() > 1e-12f))
        {
            throw new ValueException("Quat.lookAt needs a direction that is not zero");
        }

        Vector3 z = -Vector3.Normalize(forward);
        Vector3 x = Vector3.Cross(up, z);
        if (!(x.LengthSquared() > 1e-12f))
        {
            // Looking straight along the up direction: any perpendicular will do.
            x = Math.Abs(z.Y) < 0.99f ? Vector3.Cross(Vector3.UnitY, z) : Vector3.Cross(Vector3.UnitX, z);
        }

        x = Vector3.Normalize(x);
        Vector3 y = Vector3.Cross(z, x);
        var basis = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        PushQuat(L, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis)));
        return 1;
    }

    // ---- Metamethods ---------------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QuatIndex(State* L) => Guard(L, &QuatIndexBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QuatNameCall(State* L) => Guard(L, &QuatNameCallBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QuatToString(State* L) => Guard(L, &QuatToStringBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QuatMultiply(State* L) => Guard(L, &QuatMultiplyBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QuatEquals(State* L) => Guard(L, &QuatEqualsBody);

    private static int QuatIndexBody(State* L)
    {
        Quaternion q = ReadQuat(L, 1);
        string field = Stack.Is(L, 2, LuaType.Tstring) ? Stack.ReadString(L, 2) : "";
        float value = field switch
        {
            "x" => q.X,
            "y" => q.Y,
            "z" => q.Z,
            "w" => q.W,
            _ => throw new ValueException($"'{Stack.Describe(L, 2)}' is not a field of Quat (x, y, z and w are; the methods are called with a colon)"),
        };
        Lua.Pushnumber(L, value);
        return 1;
    }

    private static int QuatNameCallBody(State* L)
    {
        string method = NameCalled(L);
        Quaternion q = ReadQuat(L, 1);
        switch (method)
        {
            case "inverse":
                PushQuat(L, Quaternion.Inverse(q));
                return 1;
            case "normalized":
                PushQuat(L, Quaternion.Normalize(q));
                return 1;
            case "slerp":
                PushQuat(L, Quaternion.Slerp(q, ReadQuat(L, 2), Number(L, 3, "slerp")));
                return 1;
            case "dot":
                Lua.Pushnumber(L, Quaternion.Dot(q, ReadQuat(L, 2)));
                return 1;
            case "toEuler":
                float sinPitch = Math.Clamp(2 * ((q.W * q.X) - (q.Y * q.Z)), -1f, 1f);
                float pitch = MathF.Asin(sinPitch);
                float yaw = MathF.Atan2(2 * ((q.X * q.Z) + (q.W * q.Y)), 1 - (2 * ((q.X * q.X) + (q.Y * q.Y))));
                float roll = MathF.Atan2(2 * ((q.X * q.Y) + (q.W * q.Z)), 1 - (2 * ((q.X * q.X) + (q.Z * q.Z))));
                Lua.Pushvector(L, pitch, yaw, roll);
                return 1;
            default:
                throw new ValueException($"Quat has no method {method} (it has inverse, normalized, slerp, dot and toEuler)");
        }
    }

    private static int QuatToStringBody(State* L)
    {
        Stack.PushString(L, DescribeQuat(ReadQuat(L, 1)));
        return 1;
    }

    private static int QuatMultiplyBody(State* L)
    {
        if (!IsQuat(L, 1))
        {
            throw new ValueException($"cannot multiply {Stack.TypeName(L, 1)} by a Quat (a Quat goes on the left: q * v)");
        }

        Quaternion left = ReadQuat(L, 1);
        if (IsQuat(L, 2))
        {
            PushQuat(L, left * ReadQuat(L, 2));
            return 1;
        }

        if (Stack.Is(L, 2, LuaType.Tvector))
        {
            Vector3 rotated = Vector3.Transform(VectorArgument(L, 2, "Quat * vector"), left);
            Lua.Pushvector(L, rotated.X, rotated.Y, rotated.Z);
            return 1;
        }

        throw new ValueException($"cannot multiply a Quat by {Stack.TypeName(L, 2)} (a Quat or a vector)");
    }

    private static int QuatEqualsBody(State* L)
    {
        Lua.Pushboolean(L, IsQuat(L, 1) && IsQuat(L, 2) && ReadQuat(L, 1) == ReadQuat(L, 2) ? 1 : 0);
        return 1;
    }
}
