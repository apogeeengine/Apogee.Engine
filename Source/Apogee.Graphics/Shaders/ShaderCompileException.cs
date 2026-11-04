using System;

namespace Apogee.Graphics.Shaders;

/// <summary>A shader did not compile; <see cref="Diagnostics"/> has the compiler's messages with file and line.</summary>
public sealed class ShaderCompileException : Exception
{
    public ShaderCompileException(string moduleName, string diagnostics)
        : base($"Shader '{moduleName}' did not compile:\n{diagnostics}")
    {
        ModuleName = moduleName;
        Diagnostics = diagnostics;
    }

    private ShaderCompileException(string moduleName, string diagnostics, string message)
        : base(message)
    {
        ModuleName = moduleName;
        Diagnostics = diagnostics;
    }

    /// <summary>A shader that cannot be used for a reason that is not a compile error (no compiler, a bundle of another ABI).</summary>
    public static ShaderCompileException Unavailable(string moduleName, string message) => new(moduleName, message, message);

    public string ModuleName { get; }

    public string Diagnostics { get; }
}
