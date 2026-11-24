using System;
using System.Collections.Generic;
using Weft.Host.Editing;
using Weft.Reflection;

// An undo or redo runs the setter of a registered property, which may throw: the model reports it and keeps going.
#pragma warning disable CA1031

namespace Weft.Editor.Inspector;

/// <summary>An engine object of the session the <i>Debug &gt; Engine objects</i> window can inspect, and the name it is listed under.</summary>
public sealed record EngineObjectEntry(string Name, EngineObject Target);

/// <summary>
/// What the <i>Debug &gt; Engine objects</i> window does, with no drawing: which of the session's objects it lists (the world, the UI, physics, audio, assets, models, input, network, Steam and the
/// settings), which one is shown, and an <see cref="ObjectInspector"/> over it. Edits here are <b>live</b>: they go straight to the object's setters, they are not part of the scene and not on the
/// scene's history, and they work while the game plays. They have a history of their own (<see cref="History"/>, with <see cref="Undo"/> and <see cref="Redo"/>) that is dropped when the shown
/// object is another one (a Play or Stop replaces the session). The object is chosen by name, so the choice survives that.
/// </summary>
public sealed class EngineObjectsModel
{
    /// <summary>What the window tells the person about where these edits go.</summary>
    public const string Notice = "Edits here are live. They are not part of the scene and not on its undo history (this window has its own Undo and Redo), and they work while the game plays.";

    private readonly EditorProject _project;
    private EngineObject? _current;

    /// <summary>A model over the session of <paramref name="project"/>; it follows the project's session as it is replaced.</summary>
    public EngineObjectsModel(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        History = new EditHistory { Limit = 200 };
        Inspector = new ObjectInspector(History, entityName: NameOf);
        project.SessionReplaced += Forget;      // the window makes one model for a project and drops it with the project
    }

    /// <summary>The history of the edits made in this window (not the scene's).</summary>
    public EditHistory History { get; }

    /// <summary>The inspector that draws the shown object.</summary>
    public ObjectInspector Inspector { get; }

    /// <summary>The name of the entry shown (one of <see cref="Entries"/>); null for the first.</summary>
    public string? SelectedName { get; set; }

    /// <summary>Why the last undo or redo did not work (a setter refused the old value), or null.</summary>
    public string? Message { get; private set; }

    /// <summary>The objects of the session that are alive, in the order the window lists them. The session is looked up each time: Play and Stop replace it.</summary>
    public IReadOnlyList<EngineObjectEntry> Entries()
    {
        var session = _project.Session;
        EngineObjectEntry[] all =
        [
            new("World", session.World),
            new("UI", session.Ui),
            new("Physics", session.Physics),
            new("Audio", session.Audio),
            new("Assets", session.Assets),
            new("Models", session.Models),
            new("Input actions", session.Input),
            new("Network", session.Net),
            new("Steam", session.Steam),
            new("Settings", session.Settings),
        ];
        var alive = new List<EngineObjectEntry>(all.Length);
        foreach (EngineObjectEntry entry in all)
        {
            if (!entry.Target.IsDestroyed)
            {
                alive.Add(entry);
            }
        }

        return alive;
    }

    /// <summary>The object that is shown: the entry called <see cref="SelectedName"/>, else the first. Null when the session has none alive.</summary>
    public EngineObject? Selected
    {
        get
        {
            IReadOnlyList<EngineObjectEntry> entries = Entries();
            EngineObject? found = null;
            foreach (EngineObjectEntry entry in entries)
            {
                if (entry.Name == SelectedName)
                {
                    found = entry.Target;
                    break;
                }
            }

            found ??= entries.Count > 0 ? entries[0].Target : null;
            if (!ReferenceEquals(found, _current))
            {
                // Another object (or another session's): the steps in the history belong to the old one.
                _current = found;
                History.Clear();
                Message = null;
            }

            return found;
        }
    }

    /// <summary>Draws the shown object through <paramref name="ui"/>. True when a property was written this frame.</summary>
    public bool Draw(IInspectorUi ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        if (Selected is not { } selected)
        {
            ui.Text("The session has no engine objects.");
            return false;
        }

        return Inspector.Draw(ui, selected);
    }

    /// <summary>Takes back the last edit made in this window. False when there is none, or when the setter refused the old value (<see cref="Message"/> says so).</summary>
    public bool Undo() => Run(History.Undo);

    /// <summary>Does the last undone edit again. See <see cref="Undo"/>.</summary>
    public bool Redo() => Run(History.Redo);

    // A new session has new objects: what was edited is not there any more.
    private void Forget()
    {
        _current = null;
        History.Clear();
        Message = null;
    }

    private bool Run(Func<bool> step)
    {
        Message = null;
        _ = Selected;       // a history that belongs to another object is dropped first
        try
        {
            return step();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            return false;
        }
    }

    private string? NameOf(Entity entity)
    {
        var world = _project.World;
        return world.IsAlive(entity) ? world.GetName(entity) ?? $"#{entity.Id & 0xFFFFFFFF}" : null;
    }
}
