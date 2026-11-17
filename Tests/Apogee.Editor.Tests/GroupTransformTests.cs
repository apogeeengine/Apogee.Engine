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
