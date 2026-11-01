using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Apogee.Interop;

/// <summary>
/// Resolves the native libraries behind generated <c>DllImport</c>s. A binding assembly registers each logical library
/// name once, with the file names to try per operating system.
/// </summary>
public static class NativeLibraries
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<Assembly, Dictionary<string, string[]>> s_registered = [];

    /// <summary>Registers the candidate file names for <paramref name="logicalName"/>, the name used in the <c>DllImport</c>s of <paramref name="assembly"/>.</summary>
    public static void Register(Assembly assembly, string logicalName, string[] linux, string[] windows, string[] macos)
    {
        string[] candidates = OperatingSystem.IsWindows() ? windows : OperatingSystem.IsMacOS() ? macos : linux;

        lock (s_lock)
        {
            if (!s_registered.TryGetValue(assembly, out Dictionary<string, string[]>? libraries))
            {
                libraries = [];
                s_registered.Add(assembly, libraries);
                NativeLibrary.SetDllImportResolver(assembly, Resolve);
            }

            libraries[logicalName] = candidates;
        }
    }

    /// <summary>Loads the library registered as <paramref name="logicalName"/> for <paramref name="assembly"/>, or returns false when none of its candidates loads.</summary>
    public static bool TryLoad(Assembly assembly, string logicalName, out IntPtr handle)
    {
        handle = Resolve(logicalName, assembly, null);
        return handle != IntPtr.Zero;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string[]? candidates;
        lock (s_lock)
        {
            if (!s_registered.TryGetValue(assembly, out Dictionary<string, string[]>? libraries)
                || !libraries.TryGetValue(libraryName, out candidates))
            {
                return IntPtr.Zero;
            }
        }

        // The portable identifier, not RuntimeInformation.RuntimeIdentifier (distribution specific, "arch-x64" on Arch Linux).
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string rid = $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        foreach (string candidate in candidates)
        {
            // Next to the executable first, then the runtimes/<rid>/native layout, then the system's own search.
            foreach (string directory in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native") })
            {
                if (NativeLibrary.TryLoad(Path.Combine(directory, candidate), out IntPtr local))
                {
                    return local;
                }
            }

            if (NativeLibrary.TryLoad(candidate, out IntPtr system))
            {
                return system;
            }
        }

        return IntPtr.Zero;
    }
}
