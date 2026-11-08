using System.Threading;
using Apogee.Native.Jolt;
using JoltApi = Apogee.Native.Jolt.Jolt;

namespace Apogee.Physics;

/// <summary>Jolt's process-wide registration of its types. It is done once and never undone: shapes may outlive any one world.</summary>
internal static class PhysicsLibrary
{
    private static readonly Lock s_lock = new();
    private static bool s_initialized;

    /// <summary>
    /// Registers Jolt's allocator and types before the first use. A thread that arrives while another is still registering waits for it: returning early would let it
    /// build a world while Jolt's allocator is still a null function pointer.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (Volatile.Read(ref s_initialized))
        {
            return;
        }

        lock (s_lock)
        {
            if (s_initialized)
            {
                return;
            }

            if (!JoltApi.Init())
            {
                throw new PhysicsException("Jolt did not initialize.");
            }

            Volatile.Write(ref s_initialized, true);
        }
    }
}
