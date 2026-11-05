using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Native.Jolt;

namespace Apogee.Physics;

/// <summary>Between the engine's math types and the native layer's.</summary>
internal static class Convert
{
    public static AjVec3 ToJolt(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    public static AjQuat ToJolt(Quaternion q) => new() { X = q.X, Y = q.Y, Z = q.Z, W = q.W };

    public static AjRVec3 ToJolt(Double3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    public static Vector3 FromJolt(AjVec3 v) => new(v.X, v.Y, v.Z);

    public static Quaternion FromJolt(AjQuat q) => new(q.X, q.Y, q.Z, q.W);

    public static Double3 FromJolt(AjRVec3 v) => new(v.X, v.Y, v.Z);
}
