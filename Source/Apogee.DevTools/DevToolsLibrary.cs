using System;
using System.Collections.Generic;
using Apogee.Core.Logging;
using Apogee.Reflection;

namespace Apogee.DevTools;

/// <summary>
/// What scripts register for the dev tools: functions that draw each frame, and values to watch. The host calls <see cref="RunGui"/> once a frame
/// between <see cref="ImGuiHost.BeginFrame(float)"/> and <see cref="ImGuiHost.EndFrame"/>; the debug window shows <see cref="Watches"/>.
/// </summary>
public static class ScriptGui
{
    private static readonly List<(int Id, ScriptFunction Callback)> s_callbacks = [];
    private static readonly SortedDictionary<string, string> s_watches = new(StringComparer.Ordinal);
    private static int s_nextId;

    /// <summary>The values scripts watch, by name, as text.</summary>
    public static IReadOnlyDictionary<string, string> Watches => s_watches;

    public static int CallbackCount => s_callbacks.Count;

    internal static int Add(ScriptFunction callback)
    {
        s_callbacks.Add((++s_nextId, callback));
        return s_nextId;
    }

    internal static void Remove(int id) => s_callbacks.RemoveAll(c => c.Id == id);

    internal static void Watch(string name, string value) => s_watches[name] = value;

    /// <summary>Forgets every callback and watch (a host starting over, tests).</summary>
    public static void Clear()
    {
        s_callbacks.Clear();
        s_watches.Clear();
    }

    /// <summary>Calls the scripts' drawing functions. One that fails is logged and removed: it would only fail again every frame.</summary>
    public static void RunGui()
    {
        foreach ((int id, ScriptFunction callback) in s_callbacks.ToArray())
        {
            try
            {
                callback.Call([], []);
            }
            catch (Exception failure)
            {
                Log.Error("DevTools", $"A script's gui function failed and is removed: {failure.Message}");
                Remove(id);
            }
        }
    }
}

/// <summary>The <c>devtools</c> table of scripts: when to draw, and what to show in the debug window.</summary>
[ScriptLibrary("devtools")]
public static partial class DevToolsLibrary
{
    /// <summary>Whether an ImGui frame is open: <c>imgui</c> calls do something only then.</summary>
    [Method]
    public static bool FrameOpen() => ImGuiState.FrameOpen;

    /// <summary>Registers a function the host calls every frame inside the ImGui frame; returns an id for <c>remove_gui</c>.</summary>
    [Method]
    public static int OnGui(ScriptFunction callback)
    {
        int id = ScriptGui.Add(callback);
        ScriptScope.OnReload(() => ScriptGui.Remove(id)); // a module that is reloaded registers its drawing functions again
        return id;
    }

    [Method]
    public static void RemoveGui(int id) => ScriptGui.Remove(id);

    /// <summary>Shows a value under its name in the engine's debug window, updated whenever the script calls this again.</summary>
    [Method]
    public static void Watch(string name, Value value) => ScriptGui.Watch(name, value.ToString());
}
