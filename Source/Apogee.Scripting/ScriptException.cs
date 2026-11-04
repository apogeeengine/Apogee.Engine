using System;

namespace Apogee.Scripting;

public enum ScriptErrorKind
{
    /// <summary>The source does not compile.</summary>
    Compile,

    /// <summary>The script raised an error. The message has a traceback.</summary>
    Runtime,

    /// <summary>A call outlived its time budget.</summary>
    Timeout,

    /// <summary>A memory limit was exceeded.</summary>
    Memory,
}

/// <summary>A script failed. The message is the script's, with its traceback for a runtime error.</summary>
public sealed class ScriptException : Exception
{
    public ScriptException(ScriptErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public ScriptErrorKind Kind { get; }
}
