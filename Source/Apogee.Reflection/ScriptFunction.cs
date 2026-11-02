using System;

namespace Apogee.Reflection;

/// <summary>A function a script handed to the engine (a callback, a system). The scripting layer implements it.</summary>
public abstract class ScriptFunction
{
    /// <summary>Calls the function; returns how many results it wrote to <paramref name="results"/>. A script error is a <see cref="ValueException"/>.</summary>
    public abstract int Call(ReadOnlySpan<Value> arguments, Span<Value> results);
}
