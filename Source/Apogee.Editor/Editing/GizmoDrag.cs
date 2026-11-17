using System;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Host.Editing;

namespace Apogee.Editor.Editing;

/// <summary>
/// The life of one gizmo drag over a selection, with no ImGui in it so it is tested without a window. The viewport calls <see cref="Prepare"/> before it draws the gizmo and <see cref="Observe"/> after, with
/// whether ImGuizmo says it is being used.
/// <para>
/// Idle, a gesture is captured afresh every frame (so the frame a drag begins in already has the state from before its first change). The drag starts on the frame the gizmo reports use, and ends when it stops. A
/// drag can also be <b>cut short</b>: Play began, the world was replaced (a Play that failed), or the selection changed. The drag is dropped (it never touches its world again) and the history is sealed, so the
/// next gesture is a step of its own; no new gesture is captured until the pointer button is let go, because ImGuizmo still holds the old drag and a new one begun in the middle of it would jump.
/// </para>
/// </summary>
public sealed class GizmoDrag(EditHistory history)
{
    private World? _world;
    private int _version;
    private TransformGesture? _active;
    private TransformGesture? _candidate;
    private World? _candidateWorld;
    private int _candidateVersion;
    private bool _blocked;

    /// <summary>The drag in progress, or null.</summary>
    public TransformGesture? Active => _active;

    /// <summary>Where the gizmo is: the captured pivot, then wherever the drag has taken it. Meaningful while <see cref="Prepare"/> returned a gesture.</summary>
    public Trs Pivot { get; private set; }

    /// <summary>
    /// The gesture the gizmo is drawn for this frame: the drag in progress, or (idle) the one <paramref name="capture"/> makes now; null when there is nothing to move, while playing, or while a cut-short drag
    /// is still held. <paramref name="world"/> is the project's current world and <paramref name="selectionVersion"/> the selection's version; <paramref name="pointerDown"/> is the primary button.
    /// </summary>
    public TransformGesture? Prepare(World world, int selectionVersion, bool editing, bool pointerDown, Func<TransformGesture?> capture)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(capture);
        if (_active is not null && (!editing || !ReferenceEquals(_world, world) || selectionVersion != _version))
        {
            Drop();
        }

        if (_blocked && !pointerDown)
        {
            _blocked = false;
        }

        if (_active is not null)
        {
            return _active;
        }

        _candidate = null;
        if (!editing || _blocked)
        {
            return null;
        }

        _candidate = capture();
        _candidateWorld = world;
        _candidateVersion = selectionVersion;
        Pivot = _candidate?.Pivot ?? default;
        return _candidate;
    }

    /// <summary>Follows the gizmo after it ran: its use beginning starts the drag with what <see cref="Prepare"/> captured, and its use ending ends it (and seals the history).</summary>
    public void Observe(bool gizmoUsing)
    {
        if (_active is null)
        {
            if (gizmoUsing && _candidate is not null)
            {
                _active = _candidate;
                _world = _candidateWorld;
                _version = _candidateVersion;
            }
        }
        else if (!gizmoUsing)
        {
            history.Seal();
            _active = null;
        }
    }

    /// <summary>The pivot after a frame of the drag (see <see cref="GroupTransform.PivotAfter"/>); ignored when no drag is in progress.</summary>
    public void MoveTo(in Trs pivot)
    {
        if (_active is not null)
        {
            Pivot = pivot;
        }
    }

    private void Drop()
    {
        _active = null;
        _blocked = true;
        history.Seal();
    }
}
