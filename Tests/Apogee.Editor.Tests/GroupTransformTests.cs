using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Editor.Editing;
using Apogee.Host.Editing;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Editor.Tests;

// The expectations here are worked out by hand from the geometry (a quarter turn about Y takes +X to -Z, a stretch along an axis moves a point on that axis), not by running the same formula the code uses.

/// <summary>The maths of moving, rotating and scaling a selection about a pivot, and the gesture that drives it, over a headless world: no window, no ImGui.</summary>
public sealed class GroupTransformTests : IDisposable
{
    private const float Quarter = MathF.PI / 2f;

    private readonly World _world;

    public GroupTransformTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _world = new World();
    }

    public void Dispose() => _world.Dispose();

    private Entity Make(string name, Double3 at, Quaternion? rotation = null, Vector3? scale = null, Entity parent = default)
    {
        Entity e = _world.CreateEntity(name, parent);
        _world.Set(e, new Transform(at, rotation ?? Quaternion.Identity, scale ?? Vector3.One));
        return e;
    }

    private static Trs At(Double3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);

    private static void AssertNear(Double3 expected, Double3 actual, double tolerance = 1e-5)
    {
        Assert.True(Double3.Distance(expected, actual) < tolerance, $"expected {expected} but was {actual}");
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-5f)
    {
        Assert.True((expected - actual).Length() < tolerance, $"expected {expected} but was {actual}");
    }

    private static void AssertNear(Double3 expected, Vector3 actual, double tolerance = 1e-5) => AssertNear(expected, (Double3)actual, tolerance);

    private static WorldBox?[] NoBoxes(int count) => new WorldBox?[count];

    // ---- The pivot ----

    [Fact]
    public void TheCentreIsTheMiddleOfTheUnionOfTheBoundsWhenEveryoneHasOneElseTheMeanOfTheOrigins()
    {
        Trs[] worlds = [At(new Double3(0, 0, 0)), At(new Double3(10, 0, 0))];
        WorldBox?[] boxes =
        [
            GroupTransform.BoxOf(worlds[0], new Vector3(-1), new Vector3(1)),     // x in [-1, 1]
            GroupTransform.BoxOf(worlds[1], new Vector3(-3), new Vector3(3)),     // x in [7, 13]
        ];

        // The union spans x from -1 to 13: its middle is 6. The mean of the two origins would be 5.
        Assert.Equal(new Double3(6, 0, 0), GroupTransform.Pivot(worlds, boxes, 0, PivotMode.Center, localAxes: false).Position);
        Assert.Equal(new Double3(6, 0, 0), GroupTransform.Pivot(worlds, boxes, 0, PivotMode.Individual, localAxes: false).Position);

        // One member without bounds (a light): the means of the origins.
        boxes[1] = null;
        Assert.Equal(new Double3(5, 0, 0), GroupTransform.Pivot(worlds, boxes, 0, PivotMode.Center, localAxes: false).Position);
    }

    [Fact]
    public void TheActivePivotIsTheActiveOriginAndOneEntityIsAlwaysItsOwnOrigin()
    {
        Trs[] worlds = [At(new Double3(1, 2, 3)), At(new Double3(10, 0, 0)), At(new Double3(-4, 5, 6))];
        Assert.Equal(new Double3(10, 0, 0), GroupTransform.Pivot(worlds, NoBoxes(3), 1, PivotMode.Active, localAxes: false).Position);

        // One member with a box that is not centred on its origin: the gizmo still sits on the origin.
        Trs[] one = [At(new Double3(1, 2, 3))];
        WorldBox?[] box = [GroupTransform.BoxOf(one[0], new Vector3(0), new Vector3(10))];
        Assert.Equal(new Double3(1, 2, 3), GroupTransform.Pivot(one, box, 0, PivotMode.Center, localAxes: false).Position);
    }

    [Fact]
    public void ThePivotTurnsWithTheActiveEntityOnlyInLocalAxes()
    {
        Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter);
        Trs[] worlds = [At(new Double3(0, 0, 0)), At(new Double3(4, 0, 0), turned)];

        Trs world = GroupTransform.Pivot(worlds, NoBoxes(2), 1, PivotMode.Center, localAxes: false);
        Trs local = GroupTransform.Pivot(worlds, NoBoxes(2), 1, PivotMode.Center, localAxes: true);
        Assert.Equal(Quaternion.Identity, world.Rotation);
        Assert.Equal(turned, local.Rotation);
        Assert.Equal(Vector3.One, local.Scale);
    }

    [Fact]
    public void TheBoxOfARotatedModelCoversItsCorners()
    {
        // A cube of half-size 1 turned 45 degrees about Y reaches sqrt(2) along X and Z, and stays 1 along Y.
        Trs placed = At(new Double3(100, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
        WorldBox box = GroupTransform.BoxOf(placed, new Vector3(-1), new Vector3(1));
        AssertNear(new Double3(100 - Math.Sqrt(2), -1, -Math.Sqrt(2)), box.Min);
        AssertNear(new Double3(100 + Math.Sqrt(2), 1, Math.Sqrt(2)), box.Max);
    }

    // ---- Moving ----

    [Fact]
    public void MovingAGroupMovesEveryMemberByTheSameDoubleDelta()
    {
        // Ten million metres out a float steps by a metre; these offsets are quarters and eighths, which a double keeps exactly.
        const double far = 10_000_000;
        Quaternion tilted = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        Trs[] members = [At(new Double3(far + 0.25, 0, 5)), At(new Double3(far + 10.5, 2, -3), tilted), At(new Double3(far - 4.75, 1, 0.5))];
        Trs before = At(members[0].Position);          // a pivot on a member keeps every number a quarter or an eighth, so the sums are exact
        Trs after = At(before.Position + new Double3(0.5, 0.125, -0.25));
        Double3 delta = new(0.5, 0.125, -0.25);

        foreach (PivotMode mode in Enum.GetValues<PivotMode>())
        {
            foreach (Trs member in members)
            {
                Trs moved = GroupTransform.Apply(before, after, member, mode);
                Assert.Equal(member.Position + delta, moved.Position);       // exact: no float in the way
                Assert.Equal(member.Rotation, moved.Rotation);
                Assert.Equal(member.Scale, moved.Scale);
            }
        }
    }

    [Fact]
    public void ADragInMilliMetreStepsFarFromTheOriginKeepsTheMillimetres()
    {
        const double far = 10_000_000;
        Trs member = At(new Double3(far + 1, 0, 0));
        Trs before = At(member.Position);
        // 1 mm: below what a float can say at this distance (its step there is a metre).
        Trs moved = GroupTransform.Apply(before, At(before.Position + new Double3(0.001, 0, 0)), member, PivotMode.Center);
        Assert.InRange(moved.Position.X - (far + 1), 0.0009, 0.0011);
    }

    // ---- Rotating ----

    [Fact]
    public void RotatingAboutTheCentreSwingsTheMembersAround()
    {
        // Members on the X axis at 10 and 14: the centre is (12, 0, 0). A quarter turn about Y takes an offset along +X to -Z and one along -X to +Z (x' = x cos + z sin, z' = -x sin + z cos).
        Trs a = At(new Double3(10, 0, 0));
        Trs b = At(new Double3(14, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, Quarter));
        Trs before = GroupTransform.Pivot([a, b], NoBoxes(2), 0, PivotMode.Center, localAxes: false);
        Assert.Equal(new Double3(12, 0, 0), before.Position);
        Trs after = At(before.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter));

        Trs movedA = GroupTransform.Apply(before, after, a, PivotMode.Center);
        Trs movedB = GroupTransform.Apply(before, after, b, PivotMode.Center);

        AssertNear(new Double3(12, 0, 2), movedA.Position);        // offset (-2, 0, 0) -> (0, 0, +2)
        AssertNear(new Double3(12, 0, -2), movedB.Position);       // offset (+2, 0, 0) -> (0, 0, -2)
        AssertNear(new Vector3(0, 0, -1), Vector3.Transform(Vector3.UnitX, movedA.Rotation));   // its +X now points along -Z
        // b was already turned a quarter about X (+Y to +Z); the world turn about Y then takes +Z to +X: its +Y ends up along +X.
        AssertNear(new Vector3(1, 0, 0), Vector3.Transform(Vector3.UnitY, movedB.Rotation));
    }

    [Fact]
    public void RotatingInLocalAxesTurnsAboutTheActiveEntitiesAxis()
    {
        // The active entity is turned a quarter about Y, so its local X axis is the world's -Z. Rotating the gizmo a quarter about its own X is a quarter turn about -Z in the world,
        // which takes +Y to +X (a quarter turn about +Z takes +Y to -X, so the opposite sense takes it to +X).
        Quaternion active = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter);
        Trs a = At(new Double3(0, 0, 0), active);
        Trs b = At(new Double3(0, 2, 0));
        Trs before = GroupTransform.Pivot([a, b], NoBoxes(2), 0, PivotMode.Active, localAxes: true);
        Assert.Equal(active, before.Rotation);
        Trs after = At(before.Position, Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, Quarter), before.Rotation));   // a local turn first, then the pivot's own

        Trs movedB = GroupTransform.Apply(before, after, b, PivotMode.Active);

        AssertNear(new Double3(2, 0, 0), movedB.Position);
        AssertNear(new Vector3(1, 0, 0), Vector3.Transform(Vector3.UnitY, movedB.Rotation));
    }

    [Fact]
    public void IndividualOriginsRotateInPlace()
    {
        Trs a = At(new Double3(10, 0, 0));
        Trs b = At(new Double3(14, 0, 0));
        Trs before = GroupTransform.Pivot([a, b], NoBoxes(2), 0, PivotMode.Individual, localAxes: false);
        Trs after = At(before.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter));

        foreach (Trs member in new[] { a, b })
        {
            Trs moved = GroupTransform.Apply(before, after, member, PivotMode.Individual);
            Assert.Equal(member.Position, moved.Position);             // nobody leaves its place
            AssertNear(new Vector3(0, 0, -1), Vector3.Transform(Vector3.UnitX, moved.Rotation));
        }

        // A move still carries each of them by the same amount.
        Trs shifted = At(before.Position + new Double3(0, 3, 0));
        Assert.Equal(new Double3(14, 3, 0), GroupTransform.Apply(before, shifted, b, PivotMode.Individual).Position);
    }

    // ---- Scaling ----

    [Fact]
    public void ScalingAboutTheCentreSpreadsTheMembers()
    {
        Trs a = At(new Double3(10, 0, 0));
        Trs b = At(new Double3(14, 0, 0));
        Trs before = GroupTransform.Pivot([a, b], NoBoxes(2), 0, PivotMode.Center, localAxes: true);      // (12, 0, 0)
        Trs bigger = new(before.Position, Quaternion.Identity, new Vector3(3));
        Trs wider = new(before.Position, Quaternion.Identity, new Vector3(2, 1, 1));

        // Offsets of -2 and +2 along X, times 3: 6 and 18, and each member three times as big.
        Trs movedA = GroupTransform.Apply(before, bigger, a, PivotMode.Center);
        Trs movedB = GroupTransform.Apply(before, bigger, b, PivotMode.Center);
        AssertNear(new Double3(6, 0, 0), movedA.Position);
        AssertNear(new Double3(18, 0, 0), movedB.Position);
        AssertNear(new Vector3(3), movedA.Scale);

        // Doubled along X only: 8 and 16, wide members, and the other axes untouched.
        Trs wideA = GroupTransform.Apply(before, wider, a, PivotMode.Center);
        AssertNear(new Double3(8, 0, 0), wideA.Position);
        AssertNear(new Double3(16, 0, 0), GroupTransform.Apply(before, wider, b, PivotMode.Center).Position);
        AssertNear(new Vector3(2, 1, 1), wideA.Scale);
    }

    [Fact]
    public void IndividualScalingGrowsEachMemberInPlace()
    {
        Trs a = new(new Double3(10, 0, 0), Quaternion.Identity, new Vector3(1, 2, 1));
        Trs before = At(new Double3(12, 0, 0));
        Trs after = new(before.Position, Quaternion.Identity, new Vector3(1.5f, 1, 1));
        Trs moved = GroupTransform.Apply(before, after, a, PivotMode.Individual);
        Assert.Equal(new Double3(10, 0, 0), moved.Position);
        AssertNear(new Vector3(1.5f, 2, 1), moved.Scale);
    }

    [Fact]
    public void AStretchAlongTheWorldXOfAMemberTurnedAQuarterReachesItsLocalZ()
    {
        // Turned a quarter about Y, the member's local Z lies along the world's +X (x' = z sin) and its local X along -Z. Doubling the world's X doubles the member's Z scale, not its X.
        Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter);
        Vector3 scale = GroupTransform.StretchedScale(turned, new Vector3(1, 1, 1), Quaternion.Identity, new Vector3(2, 1, 1));
        AssertNear(new Vector3(1, 1, 2), scale);

        // Along the member's own axes (the pivot turned with it) it is the plain product.
        Vector3 own = GroupTransform.StretchedScale(turned, new Vector3(1, 2, 3), turned, new Vector3(2, 1, 0.5f));
        AssertNear(new Vector3(2, 2, 1.5f), own);

        // A ratio of exactly one leaves the scale bit for bit (a move or a rotation never drifts it).
        Assert.Equal(new Vector3(1.1f, 2.2f, 3.3f), GroupTransform.StretchedScale(turned, new Vector3(1.1f, 2.2f, 3.3f), Quaternion.Identity, Vector3.One));
    }

    // ---- The gesture, over a world ----

    private TransformGesture Begin(IReadOnlyList<Entity> selected, Entity? active, PivotMode mode, bool localAxes = false) =>
        TransformGesture.Begin(_world, selected, active, mode, localAxes, _ => null) ?? throw new InvalidOperationException("no gesture");

    [Fact]
    public void AGestureMovesTheMembersBothUnderAParentAndAtTheRoot()
    {
        // The parent is turned a quarter about Y and doubled: a child at local (1, 0, 0) is at world (0, 0, -2) (scale, then turn).
        Entity parent = Make("parent", new Double3(0, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter), new Vector3(2));
        Entity child = Make("child", new Double3(1, 0, 0), parent: parent);
        Entity loose = Make("loose", new Double3(5, 0, 0));
        AssertNear(new Double3(0, 0, -2), _world.ComputeWorldTransform(child).Position);

        TransformGesture gesture = Begin([child, loose], loose, PivotMode.Center);
        Trs after = At(gesture.Pivot.Position + new Double3(0, 0, -2));          // two metres along -Z in the world
        List<(Entity Entity, Transform Local)> result = gesture.Evaluate(after);

        // The child went two more metres along its parent's local X axis (which is the world's -Z, and the parent's scale of 2 makes a world metre half a local one): local x 1 -> 2.
        Transform childLocal = result.Single(r => r.Entity == child).Local;
        AssertNear(new Double3(2, 0, 0), childLocal.Position);
        AssertNear(new Double3(5, 0, -2), result.Single(r => r.Entity == loose).Local.Position);      // a root: its world placement is its local one
    }

    [Fact]
    public void AGestureLeavesOutWhatHasNoTransformAndCarriesTheActiveToItsMember()
    {
        Entity bare = _world.CreateEntity("bare");                                 // no Transform
        Entity top = Make("top", new Double3(0, 0, 0));
        Entity inner = Make("inner", new Double3(1, 0, 0), parent: top);
        Entity other = Make("other", new Double3(20, 0, 0));

        // `inner` is the active entity but `top` carries it (the selection's top level is [top, other] here).
        TransformGesture gesture = Begin([bare, top, other], inner, PivotMode.Active);
        Assert.Equal(new[] { top, other }, gesture.Members);
        Assert.Equal(new Double3(0, 0, 0), gesture.Pivot.Position);

        Assert.Null(TransformGesture.Begin(_world, [bare], bare, PivotMode.Center, false, _ => null));
    }

    [Fact]
    public void AGestureIsWorkedOutFromItsStartNotFromTheLastFrame()
    {
        Entity a = Make("a", new Double3(10, 0, 0));
        Entity b = Make("b", new Double3(14, 0, 0));
        TransformGesture gesture = Begin([a, b], b, PivotMode.Center);

        // Three frames of a rotation, the entities written in between: the last frame's result depends only on the last pivot.
        Trs pivot = gesture.Pivot;
        foreach (float angle in new[] { 0.3f, 0.9f, Quarter })
        {
            foreach ((Entity entity, Transform local) in gesture.Evaluate(At(pivot.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle))))
            {
                _world.Set(entity, local);
            }
        }

        AssertNear(new Double3(12, 0, 2), _world.ComputeWorldTransform(a).Position);
        AssertNear(new Double3(12, 0, -2), _world.ComputeWorldTransform(b).Position);
    }

    // ---- What the review asked for: noise, parents, distance, the scale rule ----

    private static Trs Tilted(Double3 at) => At(at, Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.7f));

    private static void AssertNear(float expected, float actual, float tolerance) => Assert.InRange(actual, expected - tolerance, expected + tolerance);

    [Fact]
    public void AMoveWithATurnedPivotIsExactAndNeverTurnsAnything()
    {
        // The pivot is the active entity's, turned (local axes). The matrix the gizmo gives back goes through float rounding, but only its translation is read for a move, and equal
        // rotations are no turn: so every member just gets the double delta, and keeps its rotation and scale bit for bit.
        const double far = 10_000_000;
        Double3 origin = new(far, 0, 0);
        Trs[] members = [Tilted(new Double3(far + 3.25, 1, 2)), At(new Double3(far + 20.5, 2, -3), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f)), Tilted(new Double3(far - 8.75, 1, 0.5))];
        Trs before = GroupTransform.Pivot(members, NoBoxes(3), 0, PivotMode.Active, localAxes: true);

        Matrix4x4 sent = before.ToMatrix(origin);
        // What the gizmo gives back: the same translation moved by (0.5, 0.125, -0.25), with a rotation that is the same turn written with other bits (the negated quaternion).
        Matrix4x4 returned = Matrix4x4.CreateFromQuaternion(Quaternion.Negate(before.Rotation)) * Matrix4x4.CreateTranslation(sent.Translation + new Vector3(0.5f, 0.125f, -0.25f));
        Trs after = GroupTransform.PivotAfter(before, GroupOperation.Move, sent, returned);

        Assert.Equal(before.Rotation, after.Rotation);
        Assert.Equal(before.Scale, after.Scale);
        Double3 delta = new(0.5, 0.125, -0.25);
        foreach (PivotMode mode in Enum.GetValues<PivotMode>())
        {
            foreach (Trs member in members)
            {
                Trs moved = GroupTransform.Apply(before, after, member, mode);
                Assert.Equal(member.Position + delta, moved.Position);
                Assert.Equal(member.Rotation, moved.Rotation);
                Assert.Equal(member.Scale, moved.Scale);
            }
        }
    }

    [Fact]
    public void ARotationOrAScaleKeepsTheDoublePositionOfThePivotBitForBit()
    {
        // The pivot's double position is never read back from the float matrix for these.
        Double3 origin = new(10_000_000, 0, 0);
        Trs pivot = new(new Double3(10_000_000.1234567, 3.3, -4.4), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), Vector3.One);
        Matrix4x4 sent = pivot.ToMatrix(origin);

        Matrix4x4 turned = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.1f)) * Matrix4x4.CreateTranslation(sent.Translation + new Vector3(1e-4f, 0, 0));
        Trs rotated = GroupTransform.PivotAfter(pivot, GroupOperation.Rotate, sent, turned);
        Assert.Equal(pivot.Position, rotated.Position);
        Assert.Equal(pivot.Scale, rotated.Scale);
        AssertNear(MathF.Sin(1.1f / 2f), rotated.Rotation.Y, 1e-5f);

        Matrix4x4 bigger = Matrix4x4.CreateScale(2, 1, 1) * Matrix4x4.CreateFromQuaternion(pivot.Rotation) * Matrix4x4.CreateTranslation(sent.Translation + new Vector3(1e-4f, 0, 0));
        Trs scaled = GroupTransform.PivotAfter(pivot, GroupOperation.Scale, sent, bigger);
        Assert.Equal(pivot.Position, scaled.Position);
        Assert.Equal(pivot.Rotation, scaled.Rotation);
        AssertNear(new Vector3(2, 1, 1), scaled.Scale, 1e-5f);
    }

    [Fact]
    public void RotatingAndScalingUnderATurnedNonUniformParentLandWhereTheMathSays()
    {
        // The parent is turned 30 degrees about Y and scaled (2, 3, 0.5). Write the gesture's locals, ask the engine for the world placements, and they are what Apply worked out.
        Entity parent = Make("parent", new Double3(5, 1, -2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6f), new Vector3(2, 3, 0.5f));
        Entity a = Make("a", new Double3(1, 0.5f, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), new Vector3(1, 2, 1), parent);
        Entity b = Make("b", new Double3(-2, 1, 0.5), Quaternion.Identity, Vector3.One, parent);
        Entity c = Make("c", new Double3(10, 0, 0));
        Entity[] members = [a, b, c];
        Trs[] starts = [.. members.Select(_world.ComputeWorldTransform)];

        foreach (bool rotate in new[] { true, false })
        {
            TransformGesture gesture = Begin(members, c, PivotMode.Center);
            Trs after = rotate
                ? At(gesture.Pivot.Position, Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0, 1, 1)), 0.9f))
                : new Trs(gesture.Pivot.Position, Quaternion.Identity, new Vector3(2, 1, 1.5f));
            foreach ((Entity entity, Transform local) in gesture.Evaluate(after))
            {
                _world.Set(entity, local);
            }

            for (int i = 0; i < members.Length; i++)
            {
                Trs expected = GroupTransform.Apply(gesture.Pivot, after, starts[i], PivotMode.Center);
                Trs actual = _world.ComputeWorldTransform(members[i]);
                AssertNear(expected.Position, actual.Position, 1e-4);
                AssertNear(Vector3.Transform(Vector3.One, expected.Rotation), Vector3.Transform(Vector3.One, actual.Rotation), 1e-5f);
                AssertNear(expected.Scale, actual.Scale, 1e-4f);
            }

            // Back to the start for the second round.
            for (int i = 0; i < members.Length; i++)
            {
                Entity parentOf = _world.GetParent(members[i]);
                Trs local = parentOf.IsNull ? starts[i] : starts[i].InLocalOf(_world.ComputeWorldTransform(parentOf));
                _world.Set(members[i], Transform.From(local));
            }
        }
    }

    [Fact]
    public void AMirroredParentCarriesAGroupTurnTheWayItLooks()
    {
        // The parent mirrors X: its children at local x = 1 and 3 are at world x = -1 and -3. Their middle is (-2, 0, 0); a quarter turn about Y puts the first, which is +1 from it, at z = -1 and the other at z = +1.
        Entity mirror = Make("mirror", new Double3(0, 0, 0), Quaternion.Identity, new Vector3(-1, 1, 1));
        Entity near = Make("near", new Double3(1, 0, 0), parent: mirror);
        Entity far = Make("far", new Double3(3, 0, 0), parent: mirror);
        AssertNear(new Double3(-1, 0, 0), _world.ComputeWorldTransform(near).Position);

        TransformGesture gesture = Begin([near, far], near, PivotMode.Center);
        AssertNear(new Double3(-2, 0, 0), gesture.Pivot.Position);
        foreach ((Entity entity, Transform local) in gesture.Evaluate(At(gesture.Pivot.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter))))
        {
            _world.Set(entity, local);
        }

        AssertNear(new Double3(-2, 0, -1), _world.ComputeWorldTransform(near).Position);
        AssertNear(new Double3(-2, 0, 1), _world.ComputeWorldTransform(far).Position);
        AssertNear(new Vector3(0, 0, -1), Vector3.Transform(Vector3.UnitX, _world.ComputeWorldTransform(near).Rotation));
    }

    [Fact]
    public void RotatingAndScalingTenMillionMetresOutAKilometreFromThePivotAgreeWithADoubleReference()
    {
        const double far = 10_000_000;
        Trs a = At(new Double3(far, 0, 0));
        Trs b = At(new Double3(far + 1000, 0, 0));
        Trs before = GroupTransform.Pivot([a, b], NoBoxes(2), 0, PivotMode.Active, localAxes: false);       // on a, one kilometre from b
        Trs quarter = At(before.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter));

        // A quarter turn about Y: b's offset (1000, 0, 0) becomes (0, 0, -1000), in plain double arithmetic; float quaternion rounding is worth about a tenth of a millimetre at that distance.
        Trs turned = GroupTransform.Apply(before, quarter, b, PivotMode.Active);
        Assert.InRange(turned.Position.X, far - 1e-3, far + 1e-3);
        Assert.InRange(turned.Position.Z, -1000 - 1e-3, -1000 + 1e-3);
        Assert.Equal(far, GroupTransform.Apply(before, quarter, a, PivotMode.Active).Position.X);   // the pivot's own member does not move at all

        // Doubled along X: b is 2000 m away, a has not moved.
        Trs wide = new(before.Position, Quaternion.Identity, new Vector3(2, 1, 1));
        Assert.Equal(far + 2000, GroupTransform.Apply(before, wide, b, PivotMode.Active).Position.X, 6);
        Assert.Equal(far, GroupTransform.Apply(before, wide, a, PivotMode.Active).Position.X);
    }

    [Fact]
    public void AMemberTurned45DegreesGetsTheRootMeanSquareOfTheRatiosAndItRisesWithTheRatio()
    {
        // Turned 45 degrees about Y, the member's X and Z lie half way between the world's X and Z. A stretch (rx, 1, 1) of the world's X would shear it; the scale that fits keeps the axes
        // perpendicular and gives both the length ratio sqrt((rx^2 + 1) / 2). Y is untouched.
        Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        float previous = 0;
        foreach (float rx in new[] { 0.5f, 1.5f, 2f, 3f })
        {
            Vector3 scale = GroupTransform.StretchedScale(turned, Vector3.One, Quaternion.Identity, new Vector3(rx, 1, 1));
            float expected = MathF.Sqrt(((rx * rx) + 1) / 2f);
            AssertNear(new Vector3(expected, 1, expected), scale, 1e-5f);
            Assert.True(scale.X > previous);
            previous = scale.X;
        }
    }

    [Fact]
    public void AScaleRatioIsNeverBelowTheMinimumTheGizmoUses()
    {
        Trs member = At(new Double3(10, 0, 0));
        Trs before = At(new Double3(12, 0, 0));
        foreach (float ratio in new[] { 0f, -1f })
        {
            Trs moved = GroupTransform.Apply(before, new Trs(before.Position, Quaternion.Identity, new Vector3(ratio, 1, 1)), member, PivotMode.Center);
            AssertNear(new Vector3(GroupTransform.MinimumRatio, 1, 1), moved.Scale, 1e-6f);
            Assert.True(moved.Position.X > 11.99);                  // squeezed onto the pivot, not mirrored past it
        }
    }

    [Fact]
    public void AMemberThatDiesAfterTheDragBeganIsLeftOut()
    {
        Entity a = Make("a", new Double3(1, 0, 0));
        Entity b = Make("b", new Double3(2, 0, 0));
        TransformGesture gesture = Begin([a, b], a, PivotMode.Center);
        _world.Destroy(b);

        List<(Entity Entity, Transform Local)> result = gesture.Evaluate(At(gesture.Pivot.Position + new Double3(1, 0, 0)));
        Assert.Equal(new[] { a }, result.Select(r => r.Entity));
    }

    // ---- The drag's life (what a window would otherwise be needed for) ----

    private static (GizmoDrag Drag, EditHistory History) NewDrag()
    {
        var history = new EditHistory();
        return (new GizmoDrag(history), history);
    }

    private static Func<TransformGesture?> Capture(World world, params Entity[] selected) =>
        () => TransformGesture.Begin(world, selected, selected[^1], PivotMode.Center, false, _ => null);

    private static SetTransformsCommand Move(World world, Entity entity, double by) =>
        SetTransformsCommand.Create(world, [(entity, Transform.At(new Double3(world.Get<Transform>(entity).Position.X + by, 0, 0)))], "Move a")!;

    [Fact]
    public void ADragWhoseWorldWasReplacedIsDroppedWithoutTouchingTheOldWorld()
    {
        // A Play that fails replaces the session and disposes its world while the button is held: the drag must not use the dead world again.
        (GizmoDrag drag, EditHistory history) = NewDrag();
        var old = new World();
        Entity inOld = old.CreateEntity("a");
        old.Set(inOld, Transform.At(new Double3(1, 0, 0)));
        Assert.NotNull(drag.Prepare(old, 1, editing: true, pointerDown: false, Capture(old, inOld)));
        drag.Observe(gizmoUsing: true);
        Assert.NotNull(drag.Active);
        history.Execute(Move(old, inOld, 1));
        old.Dispose();

        Entity inNew = Make("b", new Double3(5, 0, 0));
        TransformGesture? now = drag.Prepare(_world, 1, editing: true, pointerDown: true, Capture(_world, inNew));
        Assert.Null(now);                                           // nothing is drawn while the old drag's button is still down
        Assert.Null(drag.Active);

        drag.Observe(gizmoUsing: true);                             // still no drag
        Assert.Null(drag.Active);
        history.Execute(Move(_world, inNew, 1));
        Assert.Equal(2, history.Steps.Count);                       // sealed: the new edit is a step of its own
        Assert.NotNull(drag.Prepare(_world, 1, editing: true, pointerDown: false, Capture(_world, inNew)));        // the button was let go
    }

    [Fact]
    public void ADragCutShortIsSealedAndBlocksANewOneUntilTheButtonIsLetGo()
    {
        (GizmoDrag drag, EditHistory history) = NewDrag();
        Entity a = Make("a", new Double3(1, 0, 0));
        Entity b = Make("b", new Double3(4, 0, 0));
        TransformGesture? gesture = drag.Prepare(_world, 7, editing: true, pointerDown: true, Capture(_world, a));
        Assert.NotNull(gesture);
        drag.Observe(true);
        history.Execute(Move(_world, a, 1));
        history.Execute(Move(_world, a, 1));                        // two frames: still one step
        Assert.Single(history.Steps);

        // The selection changes in the middle of the drag (Escape, or a click elsewhere).
        Assert.Null(drag.Prepare(_world, 8, editing: true, pointerDown: true, Capture(_world, b)));
        Assert.Null(drag.Active);
        history.Execute(Move(_world, a, 1));                        // the next drag over the same entity must not merge into the cut one
        Assert.Equal(2, history.Steps.Count);

        Assert.Null(drag.Prepare(_world, 8, editing: true, pointerDown: true, Capture(_world, b)));       // button still down: blocked
        TransformGesture? again = drag.Prepare(_world, 8, editing: true, pointerDown: false, Capture(_world, b));
        Assert.NotNull(again);                                      // released: a new capture, of what is selected now
        Assert.Equal(new[] { b }, again.Members);
    }

    [Fact]
    public void PlayingCutsADragShortToo()
    {
        (GizmoDrag drag, EditHistory history) = NewDrag();
        Entity a = Make("a", new Double3(1, 0, 0));
        drag.Prepare(_world, 1, true, true, Capture(_world, a));
        drag.Observe(true);
        history.Execute(Move(_world, a, 1));

        Assert.Null(drag.Prepare(_world, 1, editing: false, pointerDown: false, Capture(_world, a)));
        Assert.Null(drag.Active);
        history.Execute(Move(_world, a, 1));
        Assert.Equal(2, history.Steps.Count);
    }

    [Fact]
    public void ADragThatEndsNormallySealsAndTheGizmoUsedAgainStartsANewOne()
    {
        (GizmoDrag drag, EditHistory history) = NewDrag();
        Entity a = Make("a", new Double3(1, 0, 0));
        for (int round = 0; round < 2; round++)
        {
            Assert.NotNull(drag.Prepare(_world, 1, true, false, Capture(_world, a)));
            drag.Observe(false);                                    // idle: no drag yet
            Assert.Null(drag.Active);
            Assert.NotNull(drag.Prepare(_world, 1, true, true, Capture(_world, a)));
            drag.Observe(true);
            Assert.NotNull(drag.Active);
            history.Execute(Move(_world, a, 1));
            history.Execute(Move(_world, a, 1));
            Assert.NotNull(drag.Prepare(_world, 1, true, false, Capture(_world, a)));
            drag.Observe(false);                                    // let go
            Assert.Null(drag.Active);
        }

        Assert.Equal(2, history.Steps.Count);                       // two drags, two steps
    }
}

/// <summary>A drag over a selection through the editor project: the top level only, and one undo step.</summary>
[Collection("editor")]
public sealed class GroupTransformProjectTests
{
    [Fact]
    public void ChildrenOfSelectedParentsAreNotMovedTwice()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        World world = rig.World;
        Entity parent = world.CreateEntity("parent");
        world.Set(parent, Transform.At(new Double3(100, 0, 0)));
        Entity child = world.CreateEntity("child", parent);
        world.Set(child, Transform.At(new Double3(1, 0, 0)));
        Entity loose = world.CreateEntity("loose");
        world.Set(loose, Transform.At(new Double3(-50, 0, 0)));

        project.Selected.Set([parent, child, loose], child);
        TransformGesture gesture = TransformGesture.Begin(world, project.Selected.TopLevel(), project.Selected.Active, PivotMode.Center, false, _ => null)!;
        Assert.Equal(new[] { parent, loose }, gesture.Members);                          // the child rides on its parent

        Trs after = new(gesture.Pivot.Position + new Double3(5, 0, 0), Quaternion.Identity, Vector3.One);
        project.History.Execute(SetTransformsCommand.Create(project.WorldRef, gesture.Evaluate(after).Select(e => (e.Entity, e.Local)), "Move 3 entities")!);

        Assert.Equal(105, world.ComputeWorldTransform(parent).Position.X);
        Assert.Equal(106, world.ComputeWorldTransform(child).Position.X);        // 101 + 5 once, not 101 + 10
        Assert.Equal(1, world.Get<Transform>(child).Position.X);                 // its local placement did not change at all
        Assert.Equal(-45, world.ComputeWorldTransform(loose).Position.X);

        project.History.Undo();
        Assert.Equal(100, world.ComputeWorldTransform(parent).Position.X);
        Assert.Equal(101, world.ComputeWorldTransform(child).Position.X);
        Assert.Equal(-50, world.ComputeWorldTransform(loose).Position.X);
    }
}
