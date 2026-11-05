using System.Threading;
using Apogee.Native.Jolt;
using JoltApi = Apogee.Native.Jolt.Jolt;

namespace Apogee.Physics;

/// <summary>Jolt's process-wide registration of its types. It is done once and never undone: shapes may outlive any one world.</summary>
internal static class PhysicsLibrary
{
    private static int s_initialized;

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref s_initialized, 1) == 0 && !JoltApi.Init())
        {
            Volatile.Write(ref s_initialized, 0);
            throw new PhysicsException("Jolt did not initialize.");
        }
    }
}
