using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Core.Numerics;

namespace Apogee.Editor.Editing;

/// <summary>Where the gizmo sits when several entities are selected, and what a rotation or a scale turns around.</summary>
public enum PivotMode
{
    /// <summary>The centre of the selection: of its bounds when every member has some, otherwise the mean of the origins. One entity is its own origin.</summary>
    Center,

    /// <summary>The origin of the active entity (the last one picked).</summary>
    Active,

    /// <summary>The gizmo sits at the centre, but each entity rotates and scales about its own origin; a move still carries every entity by the same amount.</summary>
    Individual,
}

/// <summary>What the gizmo is doing to the pivot.</summary>
public enum GroupOperation
{
    /// <summary>Translating.</summary>
    Move,

    /// <summary>Rotating.</summary>
    Rotate,

    /// <summary>Scaling.</summary>
    Scale,
}

/// <summary>An axis-aligned box in the double-precision world.</summary>
public readonly record struct WorldBox(Double3 Min, Double3 Max)
{
    /// <summary>The middle of the box.</summary>
    public Double3 Center => (Min + Max) * 0.5;
}

/// <summary>
/// The maths of moving, rotating and scaling a group of entities with one gizmo, as plain functions of placements (<see cref="Trs"/>), so it is tested without a window.
/// <para>
/// The gizmo works on a <b>pivot</b>: a placement in the world. A drag takes the pivot from <c>before</c> to <c>after</c>. Every member then makes the same move, seen from the pivot: its offset from the
/// pivot is stretched by the scale ratio (along the pivot's axes), swung by the rotation delta, and carried by the translation delta. All of this happens in world space; the caller turns each result into a
/// local placement under the member's parent. Positions stay <c>double</c> and only differences (a delta, an offset) become <c>float</c>, so a group a million metres from the origin keeps its precision.
/// </para>
/// <para>
/// Rotations: <see cref="Quaternion.Concatenate"/> applies its first argument first. A world-space turn <c>D</c> that takes a rotation <c>Rb</c> to <c>Ra</c> is therefore
/// <c>Concatenate(Inverse(Rb), Ra)</c> (undo <c>Rb</c>, then do <c>Ra</c>), and a member's rotation after the turn is <c>Concatenate(member, D)</c>.
/// </para>
/// </summary>
public static class GroupTransform
{
    /// <summary>The smallest scale ratio <see cref="Apply"/> uses on an axis.</summary>
    public const float MinimumRatio = 1e-3f;

    /// <summary>
    /// The world box of a model's local box (<paramref name="localMin"/>, <paramref name="localMax"/>) placed at <paramref name="world"/>: the box of its eight corners, which are carried in double.
    /// </summary>
    public static WorldBox BoxOf(in Trs world, Vector3 localMin, Vector3 localMax)
    {
        Double3 min = default;
        Double3 max = default;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? localMin.X : localMax.X,
                (i & 2) == 0 ? localMin.Y : localMax.Y,
                (i & 4) == 0 ? localMin.Z : localMax.Z);
            Double3 point = world.TransformPoint(corner);
            min = i == 0 ? point : Double3.Min(min, point);
            max = i == 0 ? point : Double3.Max(max, point);
        }

        return new WorldBox(min, max);
    }

    /// <summary>
    /// The pivot of a selection, as a placement with scale one (the gizmo's own scale is the ratio of a drag). <paramref name="worlds"/> are the members' world placements,
    /// <paramref name="boxes"/> their world bounds (null for one without a model), <paramref name="active"/> the index of the active member. The position is the active member's origin for
    /// <see cref="PivotMode.Active"/>; for <see cref="PivotMode.Center"/> and <see cref="PivotMode.Individual"/> it is the centre of the union of the boxes when every member has a box, otherwise the mean of the
    /// origins; a single member is always its own origin. The rotation is the active member's when <paramref name="localAxes"/> (the gizmo is in local mode, or scaling), otherwise the identity.
    /// </summary>
    public static Trs Pivot(IReadOnlyList<Trs> worlds, IReadOnlyList<WorldBox?> boxes, int active, PivotMode mode, bool localAxes)
    {
        if (worlds.Count == 0)
        {
            throw new ArgumentException("A pivot needs at least one entity.", nameof(worlds));
        }

        if (boxes.Count != worlds.Count)
        {
            throw new ArgumentException("There is one box (or none) for each placement.", nameof(boxes));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(active);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(active, worlds.Count);

        Double3 position = mode == PivotMode.Active || worlds.Count == 1 ? worlds[active].Position : CenterOf(worlds, boxes);
        Quaternion rotation = localAxes ? worlds[active].Rotation : Quaternion.Identity;
        return new Trs(position, rotation, Vector3.One);
    }

    /// <summary>The centre of the union of the boxes when every member has one, otherwise the mean of the origins.</summary>
    private static Double3 CenterOf(IReadOnlyList<Trs> worlds, IReadOnlyList<WorldBox?> boxes)
    {
        bool allBoxed = true;
        foreach (WorldBox? box in boxes)
        {
            allBoxed &= box is not null;
        }

        if (allBoxed)
        {
            Double3 min = boxes[0]!.Value.Min;
            Double3 max = boxes[0]!.Value.Max;
            for (int i = 1; i < boxes.Count; i++)
            {
                min = Double3.Min(min, boxes[i]!.Value.Min);
                max = Double3.Max(max, boxes[i]!.Value.Max);
            }

            return new WorldBox(min, max).Center;
        }

        // The mean as an offset from the first origin: the offsets are small, so the sum does not lose what the big coordinates carry.
        Double3 first = worlds[0].Position;
        Double3 sum = Double3.Zero;
        foreach (Trs placement in worlds)
        {
            sum += placement.Position - first;
        }

        return first + (sum / worlds.Count);
    }

    /// <summary>
    /// Where a member goes when the pivot goes from <paramref name="pivotBefore"/> to <paramref name="pivotAfter"/>, as a world placement. <paramref name="entityWorld"/> is the member's placement when the drag
    /// began (not the last frame's: every frame is worked out from the start, so nothing accumulates).
    /// <list type="bullet">
    /// <item><description>Translation: the delta <c>after - before</c> is taken in double and added to the member's position.</description></item>
    /// <item><description>Rotation: the world turn from the pivot's rotation before to after swings the member's offset from the pivot and is added to its own rotation. In <see cref="PivotMode.Individual"/> the offset
    /// does not move: the member turns about its own origin.</description></item>
    /// <item><description>Scale: the ratio <c>after.Scale / before.Scale</c> is a stretch along the pivot's axes. It scales the member's offset and, mapped onto the member's own axes, its scale (see
    /// <see cref="StretchedScale"/>). A stretch along axes that are not the member's own would shear it, which a <see cref="Trs"/> cannot hold: the scale is then an approximation.</description></item>
    /// </list>
    /// </summary>
    public static Trs Apply(in Trs pivotBefore, in Trs pivotAfter, in Trs entityWorld, PivotMode mode)
    {
        // The gizmo never goes below this (ImGuizmo clamps its scale at 0.001): a zero or negative ratio would flatten or mirror a member and leave a scale that cannot be divided by. It is clamped, not refused.
        Vector3 ratio = Vector3.Max(pivotAfter.Scale / pivotBefore.Scale, new Vector3(MinimumRatio));

        // Equal rotations are no turn at all, bit for bit: Concatenate(Inverse(R), R) is only the identity up to float rounding, and a move or a scale must not turn anything by that much.
        Quaternion turn = pivotAfter.Rotation == pivotBefore.Rotation
            ? Quaternion.Identity
            : Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Inverse(pivotBefore.Rotation), pivotAfter.Rotation));

        // The position is "where it was, plus the pivot's translation, plus how far the swing and the stretch move the offset". The last part is zero when only the pivot moved, so a pure move is exact in double.
        Double3 position = entityWorld.Position + (pivotAfter.Position - pivotBefore.Position);
        if (mode != PivotMode.Individual)
        {
            Vector3 offset = (Vector3)(entityWorld.Position - pivotBefore.Position);
            Vector3 moved = SwingAndStretch(offset, pivotBefore.Rotation, ratio, turn);
            position += moved - offset;
        }

        Quaternion rotation = turn == Quaternion.Identity ? entityWorld.Rotation : Quaternion.Normalize(Quaternion.Concatenate(entityWorld.Rotation, turn));      // no turn: no drift
        Vector3 scale = StretchedScale(entityWorld.Rotation, entityWorld.Scale, pivotBefore.Rotation, ratio);
        return new Trs(position, rotation, scale);
    }

    /// <summary>
    /// An offset from the pivot after the stretch and the swing: stretched by <paramref name="ratio"/> along the axes of <paramref name="pivotRotation"/>, then turned by <paramref name="turn"/> (a world turn).
    /// </summary>
    public static Vector3 SwingAndStretch(Vector3 offset, Quaternion pivotRotation, Vector3 ratio, Quaternion turn)
    {
        // Each step is skipped when it does nothing, so a move gives the offset back bit for bit (the round trip through the pivot's axes and back would not).
        Vector3 result = offset;
        if (ratio != Vector3.One)
        {
            Vector3 inPivotAxes = Vector3.Transform(result, Quaternion.Inverse(pivotRotation));
            result = Vector3.Transform(inPivotAxes * ratio, pivotRotation);
        }

        return turn == Quaternion.Identity ? result : Vector3.Transform(result, turn);
    }

    /// <summary>
    /// The pivot after one frame of the gizmo, from the matrix that was sent to it (<paramref name="sent"/>, the pivot placed relative to the camera) and the one it gave back (<paramref name="returned"/>), keeping
    /// everything the operation does not change exactly as it was: a move takes only the float difference of the two translations and adds it to the double position (so the pivot's position is never rounded
    /// to a float relative to the camera), a rotation takes only the rotation, a scale only the scale. Rounding noise in the other parts of the matrix therefore never moves or turns the group. A rotation or scale from a matrix that
    /// cannot be taken apart gives back <paramref name="pivotNow"/>.
    /// </summary>
    public static Trs PivotAfter(in Trs pivotNow, GroupOperation operation, in Matrix4x4 sent, in Matrix4x4 returned)
    {
        if (operation == GroupOperation.Move)
        {
            return new Trs(pivotNow.Position + (Double3)(returned.Translation - sent.Translation), pivotNow.Rotation, pivotNow.Scale);
        }

        if (!Matrix4x4.Decompose(returned, out Vector3 scale, out Quaternion rotation, out _))
        {
            return pivotNow;
        }

        return operation == GroupOperation.Rotate
            ? new Trs(pivotNow.Position, rotation, pivotNow.Scale)
            : new Trs(pivotNow.Position, pivotNow.Rotation, scale);
    }

    /// <summary>
    /// A member's scale after the pivot's stretch <paramref name="ratio"/> (along the axes of <paramref name="pivotRotation"/>). Each of the member's own axes is the world direction its rotation gives it; the stretch
    /// carries that direction to a longer or shorter one, and the member's scale along that axis is multiplied by the length ratio. When the member's axes line up with the pivot's, this is just
    /// <c>scale * ratio</c> per axis (what the gizmo shows); when they are turned a quarter turn, the ratios swap axes with them; in between the stretch would shear the member and the lengths are the best
    /// scale that fits. A ratio of one (a move or a rotation) leaves the scale exactly as it was.
    /// </summary>
    public static Vector3 StretchedScale(Quaternion entityRotation, Vector3 scale, Quaternion pivotRotation, Vector3 ratio)
    {
        if (ratio == Vector3.One)
        {
            return scale;
        }

        // The member's axes as seen in the pivot's frame, where the stretch is a plain per-axis multiplication.
        Quaternion toPivotFrame = Quaternion.Concatenate(entityRotation, Quaternion.Inverse(pivotRotation));
        float Factor(Vector3 axis)
        {
            Vector3 inPivotFrame = Vector3.Transform(axis, toPivotFrame);
            return (inPivotFrame * ratio).Length() / inPivotFrame.Length();
        }

        return new Vector3(scale.X * Factor(Vector3.UnitX), scale.Y * Factor(Vector3.UnitY), scale.Z * Factor(Vector3.UnitZ));
    }
}
