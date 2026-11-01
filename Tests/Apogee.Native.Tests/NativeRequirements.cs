using System;
using System.Linq;
using System.Reflection;
using Apogee.Interop;
using Xunit;

namespace Apogee.Native.Tests;

/// <summary>
/// A test that needs a native library skips when the library is not installed, unless CI says it must be there:
/// <c>APOGEE_REQUIRE_NATIVE</c> lists the libraries (comma separated logical names) that have to load on that machine, and
/// the test fails instead, so a binary that does not load (a glibc that is too new, a missing file) cannot hide as a skip.
/// </summary>
internal static class NativeRequirements
{
    public static IntPtr Require(Assembly assembly, string name)
    {
        if (NativeLibraries.TryLoad(assembly, name, out IntPtr handle))
        {
            return handle;
        }

        string[] required = (Environment.GetEnvironmentVariable("APOGEE_REQUIRE_NATIVE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (required.Contains(name, StringComparer.Ordinal))
        {
            Assert.Fail($"The native library '{name}' is required here (APOGEE_REQUIRE_NATIVE) but did not load.");
        }

        Assert.Skip($"The native library '{name}' is not installed.");
        return IntPtr.Zero;
    }
}
