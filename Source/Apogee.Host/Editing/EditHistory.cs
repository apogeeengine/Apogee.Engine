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
    // Each step carries a revision: a number for the state of the world after it. The state before the oldest step has one too (_baseRevision), so every reachable state is named.
    private readonly List<(IEditCommand Command, long Revision)> _undo = [];
    private readonly List<(IEditCommand Command, long Revision)> _redo = [];
    private bool _sealed = true;
    private long _revisions;
    private long _baseRevision;
    private long _savedRevision;

    /// <summary>How many steps are kept; the oldest go first.</summary>
    public int Limit { get; set; } = 500;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoName => _undo.Count > 0 ? _undo[^1].Command.Name : null;

    public string? RedoName => _redo.Count > 0 ? _redo[^1].Command.Name : null;

    /// <summary>The steps that can be undone, oldest first.</summary>
    public IReadOnlyList<string> Steps => _undo.ConvertAll(c => c.Command.Name);

    /// <summary>
    /// True when the world is in the state it had at the last <see cref="MarkSaved"/> (a new history starts at its save point): undoing back to it, or redoing up to it, is clean again. A state that the
    /// <see cref="Limit"/> pushed out of the history cannot be reached any more, so it is never the save point again until the next <see cref="MarkSaved"/>.
    /// </summary>
    public bool IsAtSavePoint => Revision == _savedRevision;

    private long Revision => _undo.Count > 0 ? _undo[^1].Revision : _baseRevision;

    /// <summary>Raised when the history changed (a command ran, undo, redo, clear): the editor marks the scene as modified.</summary>
    public event Action? Changed;

    /// <summary>Runs the command and records it, folding it into the last step when that allows it and no <see cref="Seal"/> came between.</summary>
    public void Execute(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Apply();
        _redo.Clear();
        if (!_sealed && _undo.Count > 0 && _undo[^1].Command.TryMerge(command))
        {
            _undo[^1] = (_undo[^1].Command, ++_revisions);      // the state changed: it is not the one that was saved
            Changed?.Invoke();
            return;
        }

        _undo.Add((command, ++_revisions));
        if (_undo.Count > Limit)
        {
            int dropped = _undo.Count - Limit;
            _baseRevision = _undo[dropped - 1].Revision;
            _undo.RemoveRange(0, dropped);
        }

        _sealed = false;
        Changed?.Invoke();
    }

    /// <summary>Ends the gesture: the following command is a step of its own even if it could merge with the last (a drag was let go of).</summary>
    public void Seal() => _sealed = true;

    /// <summary>The world was saved in the state it is in now: that is the save point. It also ends the gesture, so a change after the save is a step of its own.</summary>
    public void MarkSaved()
    {
        _savedRevision = Revision;
        _sealed = true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        (IEditCommand command, long revision) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Undo();
        _redo.Add((command, revision));
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

        (IEditCommand command, long revision) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Apply();
        _undo.Add((command, revision));
        _sealed = true;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Forgets every step: a new scene was opened, or the world was restored under the history's feet. The world stays as it is, so a history that was at its save point is still at it and one that was
    /// not stays away from it (the caller follows with <see cref="MarkSaved"/> when the world now matches a file).
    /// </summary>
    public void Clear()
    {
        bool atSavePoint = IsAtSavePoint;
        _undo.Clear();
        _redo.Clear();
        _baseRevision = ++_revisions;
        _savedRevision = atSavePoint ? _baseRevision : -1;
        _sealed = true;
        Changed?.Invoke();
    }
}
