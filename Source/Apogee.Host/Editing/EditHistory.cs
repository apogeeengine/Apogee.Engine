using System;
using System.Collections.Generic;

namespace Apogee.Host.Editing;

/// <summary>One undoable edit of a world.</summary>
public interface IEditCommand
{
    /// <summary>What the Edit menu says: "Move Cube", "Delete Light".</summary>
    string Name { get; }

    /// <summary>Does the edit (again, when redoing).</summary>
    void Apply();

    void Undo();

    /// <summary>
    /// Lets the command before <paramref name="following"/> take it in, so that a whole drag is one step: <paramref name="following"/> has been done already; the command folds its result in (its "after") and returns true.
    /// Most commands return false.
    /// </summary>
    bool TryMerge(IEditCommand following);
}

/// <summary>
/// The undo and redo history of an editor: commands are executed through it, and it can take them back and do them again. A new command after an undo drops what could have been redone. Consecutive
/// commands that merge (the many small changes of a drag) are one step until <see cref="Seal"/> is called, which the editor does when the gesture ends.
/// </summary>
public sealed class EditHistory
{
    private readonly List<IEditCommand> _undo = [];
    private readonly List<IEditCommand> _redo = [];
    private bool _sealed = true;

    /// <summary>How many steps are kept; the oldest go first.</summary>
    public int Limit { get; set; } = 500;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;

    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    /// <summary>The steps that can be undone, oldest first.</summary>
    public IReadOnlyList<string> Steps => _undo.ConvertAll(c => c.Name);

    /// <summary>Raised when the history changed (a command ran, undo, redo, clear): the editor marks the scene as modified.</summary>
    public event Action? Changed;

    /// <summary>Runs the command and records it, folding it into the last step when that allows it and no <see cref="Seal"/> came between.</summary>
    public void Execute(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Apply();
        _redo.Clear();
        if (!_sealed && _undo.Count > 0 && _undo[^1].TryMerge(command))
        {
            Changed?.Invoke();
            return;
        }

        _undo.Add(command);
        if (_undo.Count > Limit)
        {
            _undo.RemoveRange(0, _undo.Count - Limit);
        }

        _sealed = false;
        Changed?.Invoke();
    }

    /// <summary>Ends the gesture: the following command is a step of its own even if it could merge with the last (a drag was let go of).</summary>
    public void Seal() => _sealed = true;

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        IEditCommand command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Undo();
        _redo.Add(command);
        _sealed = true;
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        IEditCommand command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Apply();
        _undo.Add(command);
        _sealed = true;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Forgets everything: a new scene was opened, or the world was restored under the history's feet.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _sealed = true;
        Changed?.Invoke();
    }
}
