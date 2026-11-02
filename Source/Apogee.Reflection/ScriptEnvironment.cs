using System;

namespace Apogee.Reflection;

/// <summary>
/// Which script is calling the engine right now, on this thread: the scripting layer sets it around every method a script calls, so that an engine class that does work for the script (the UI
/// loading a document that has scripts of its own) can run that work as the same owner, under the same limits. Null outside a call from a script.
/// </summary>
public static class ScriptCaller
{
    [ThreadStatic]
    private static object? s_current;

    /// <summary>The caller as the scripting layer identifies it (an opaque object to everyone else).</summary>
    public static object? Current => s_current;

    public static Restore Enter(object? caller)
    {
        object? previous = s_current;
        s_current = caller;
        return new Restore(previous);
    }

    public readonly struct Restore(object? previous) : IDisposable
    {
        public void Dispose() => s_current = previous;
    }
}

/// <summary>
/// A place for the scripts of one thing to run in, such as one UI document: a table of globals of its own that falls back to the caller's, so the functions a <c>&lt;script&gt;</c> defines are visible to the
/// document's <c>onclick="..."</c> attributes and to nothing else. It runs as the script that made it (its owner, its memory and time limits).
/// </summary>
public abstract class ScriptEnvironment : IDisposable
{
    /// <summary>Who the environment's scripts run as (the same object <see cref="ScriptCaller.Current"/> gives while that script calls the engine).</summary>
    public abstract object Owner { get; }

    /// <summary>Whether the owner of this environment may read the file (a script a document names): a mod can read its own files and the base game's, not another mod's.</summary>
    public abstract bool CanRead(string path);

    /// <summary>Sets a variable the scripts of the environment see, such as <c>document</c>.</summary>
    public abstract void SetGlobal(string name, Value value);

    /// <summary>Runs a chunk (its functions and variables stay in the environment). <paramref name="chunkName"/> is <c>@path</c> for a file, so that relative <c>require</c>s work, or <c>=name</c>. Throws when it fails.</summary>
    public abstract void Run(string chunkName, string source);

    /// <summary>Compiles <paramref name="body"/> as the body of a function with the given parameter names (<c>"event, element"</c>), in the environment. Throws when it does not compile.</summary>
    public abstract ScriptFunction Compile(string chunkName, string parameters, string body);

    public abstract void Dispose();
}

/// <summary>Makes <see cref="ScriptEnvironment"/>s; the scripting layer implements it.</summary>
public interface IScriptEnvironments
{
    /// <summary>A new environment for the script that is calling (<see cref="ScriptCaller.Current"/>), or the base game's when none is.</summary>
    ScriptEnvironment Create();
}
