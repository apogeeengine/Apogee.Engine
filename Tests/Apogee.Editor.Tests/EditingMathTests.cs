using System;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Editor.Editing;
using Apogee.Reflection;
using Apogee.Rendering;
using Xunit;

namespace Apogee.Editor.Tests;

/// <summary>The editor's logic with no window and no GPU: the field codec, the camera and picking.</summary>
public sealed unsafe class EditingMathTests
{
    private static double[] RoundTrip(FieldKind kind, params double[] values)
    {
        byte* memory = stackalloc byte[32];
        new Span<byte>(memory, 32).Clear();
        FieldCodec.Write(memory, kind, values);
        return FieldCodec.Read(memory, kind);
    }

    // ---- The field codec ----

    [Fact]
    public void EveryKindOfFieldReadsBackWhatWasWritten()
    {
        Assert.Equal([1.0], RoundTrip(FieldKind.Bool, 1));
        Assert.Equal([0.0], RoundTrip(FieldKind.Bool, 0));
        Assert.Equal([-5.0], RoundTrip(FieldKind.Int8, -5));
        Assert.Equal([200.0], RoundTrip(FieldKind.UInt8, 200));
        Assert.Equal([-30000.0], RoundTrip(FieldKind.Int16, -30000));
        Assert.Equal([60000.0], RoundTrip(FieldKind.UInt16, 60000));
        Assert.Equal([-2000000000.0], RoundTrip(FieldKind.Int32, -2000000000));
        Assert.Equal([4000000000.0], RoundTrip(FieldKind.UInt32, 4000000000));
        Assert.Equal([-9000000000000.0], RoundTrip(FieldKind.Int64, -9000000000000));
        Assert.Equal([9000000000000.0], RoundTrip(FieldKind.UInt64, 9000000000000));
        Assert.Equal([0.25], RoundTrip(FieldKind.Float32, 0.25));
        Assert.Equal([1e12 + 0.125], RoundTrip(FieldKind.Float64, 1e12 + 0.125));
        Assert.Equal([1.0, 2.0, 3.5], RoundTrip(FieldKind.Vector3, 1, 2, 3.5));
        Assert.Equal([1e12 + 0.5, -2.0, 3.0], RoundTrip(FieldKind.Double3, 1e12 + 0.5, -2, 3));   // a double position keeps its precision
        Assert.Equal([42.0], RoundTrip(FieldKind.Entity, 42));
    }

    [Fact]
    public void AnIntegerIsRoundedAndClampedToItsType()
    {
        Assert.Equal([127.0], RoundTrip(FieldKind.Int8, 1000));
        Assert.Equal([-128.0], RoundTrip(FieldKind.Int8, -1000));
        Assert.Equal([0.0], RoundTrip(FieldKind.UInt8, -3));
        Assert.Equal([255.0], RoundTrip(FieldKind.UInt8, 255.4));
        Assert.Equal([3.0], RoundTrip(FieldKind.Int32, 2.6));
        Assert.Equal([0.0], RoundTrip(FieldKind.Int32, double.NaN));
        Assert.Throws<ArgumentException>(() => RoundTrip(FieldKind.Vector3, 1, 2));          // three numbers or none
    }

    [Fact]
    public void ARotationIsEditedAsAnglesAndComesBackAsTheSameRotation()
    {
        foreach ((double pitch, double yaw, double roll) in new[] { (0.0, 0.0, 0.0), (30.0, 45.0, 10.0), (-60.0, 170.0, -20.0), (10.0, -100.0, 80.0), (85.0, 0.0, 0.0) })
        {
            double[] angles = RoundTrip(FieldKind.Quaternion, pitch, yaw, roll);
            Assert.Equal(pitch, angles[0], 0.05);
            Assert.Equal(yaw, angles[1], 0.05);
            Assert.Equal(roll, angles[2], 0.05);
        }

        // Straight up, yaw and roll turn about the same axis: the rotation survives even though the angles split differently.
        Quaternion up = FieldCodec.FromEuler(MathF.PI / 2, 0.4f, 0.3f);
        (float p, float y, float r) = FieldCodec.ToEuler(up);
        Quaternion again = FieldCodec.FromEuler(p, y, r);
        Assert.True(MathF.Abs(Quaternion.Dot(up, again)) > 0.999f);
    }

    [Fact]
    public void WidgetsFollowTheKindOfField()
    {
        Assert.Equal(FieldWidget.Toggle, FieldCodec.WidgetOf(FieldKind.Bool));
        Assert.Equal(FieldWidget.WholeNumber, FieldCodec.WidgetOf(FieldKind.UInt16));
        Assert.Equal(FieldWidget.Number, FieldCodec.WidgetOf(FieldKind.Float64));
        Assert.Equal(FieldWidget.Vector, FieldCodec.WidgetOf(FieldKind.Double3));
        Assert.Equal(FieldWidget.Rotation, FieldCodec.WidgetOf(FieldKind.Quaternion));
        Assert.Equal(FieldWidget.EntityReference, FieldCodec.WidgetOf(FieldKind.Entity));
        Assert.Equal(3, FieldCodec.CountOf(FieldKind.Quaternion));
        Assert.Equal(1, FieldCodec.CountOf(FieldKind.Float32));
    }

    // ---- The camera ----

    [Fact]
    public void TheCameraOrbitsPansZoomsAndFliesAroundItsTarget()
    {
        var camera = new EditorCamera { Target = new Double3(0, 1, 0), Distance = 10, Yaw = 0, Pitch = 0 };
        Assert.True(Double3.Distance(camera.Position, new Double3(0, 1, 10)) < 1e-4);   // looking down -Z from 10 meters back

        camera.Orbit(MathF.PI / 2, 0);                                                    // a quarter turn to the left
        Assert.True(Double3.Distance(camera.Position, new Double3(10, 1, 0)) < 1e-3, camera.Position.ToString());
        Assert.True(Double3.Distance(camera.Target, new Double3(0, 1, 0)) < 1e-6);        // the target did not move

        camera.Orbit(0, 100);                                                             // pitch stops short of straight up
        Assert.True(camera.Pitch < MathF.PI / 2);

        camera.Pitch = 0;
        camera.Yaw = 0;
        camera.Pan(0.1f, 0.05f);
        Assert.True(camera.Target.X < 0 && camera.Target.Y > 1);                          // dragging right moves the scene right, so the camera goes left
        double before = camera.Distance;
        camera.Zoom(1);
        Assert.True(camera.Distance < before);
        camera.Zoom(-1);
        Assert.Equal(before, camera.Distance, 1e-4);
        camera.Distance = 0.001f;
        camera.Zoom(10);
        Assert.True(camera.Distance >= 0.01f);

        var fly = new EditorCamera { Target = Double3.Zero, Yaw = 0, Pitch = 0 };
        fly.Fly(new Vector3(0, 0, 4), 0.5f);                                              // forward at 4 m/s for half a second
        Assert.True(Double3.Distance(fly.Target, new Double3(0, 0, -2)) < 1e-4);

        var renderer = new Camera();
        camera.ApplyTo(renderer);
        Assert.Equal(camera.Position, renderer.Position);
        Assert.Equal(camera.Yaw, renderer.Yaw);
    }

    [Fact]
    public void FramingPutsTheWholeSphereInView()
    {
        var camera = new EditorCamera { FovDegrees = 60 };
        camera.Frame(new Double3(5, 2, 5), 3);
        Assert.True(Double3.Distance(camera.Target, new Double3(5, 2, 5)) < 1e-9);
        // At the fitted distance the sphere's angular radius is less than half the field of view.
        float angular = MathF.Asin(3f / camera.Distance);
        Assert.True(angular < camera.FovDegrees * MathF.PI / 360f);
    }

    // ---- Picking ----

    [Fact]
    public void ThePixelInTheMiddleOfTheViewportLooksAlongTheCamera()
    {
        var camera = new Camera { Position = new Double3(0, 2, 10), Yaw = 0, Pitch = 0 };
        PickRay middle = Picking.RayThrough(camera, new Vector2(400, 300), new Vector2(800, 600));
        Assert.True(Vector3.Distance(middle.Direction, new Vector3(0, 0, -1)) < 1e-5f);

        PickRay topRight = Picking.RayThrough(camera, new Vector2(800, 0), new Vector2(800, 600));
        Assert.True(topRight.Direction.X > 0 && topRight.Direction.Y > 0 && topRight.Direction.Z < 0);
        Assert.Equal(1f, topRight.Direction.Length(), 1e-5f);
    }

    [Fact]
    public void ARayHitsABoxWhereItIsPlacedRotatedAndScaled()
    {
        var ray = new PickRay(new Double3(0, 0, 10), new Vector3(0, 0, -1));
        var min = new Vector3(-1, -1, -1);
        var max = new Vector3(1, 1, 1);

        Assert.Equal(9f, Picking.HitBox(ray, new Trs(Double3.Zero, Quaternion.Identity, Vector3.One), min, max)!.Value, 1e-4f);
        Assert.Equal(5f, Picking.HitBox(ray, new Trs(Double3.Zero, Quaternion.Identity, new Vector3(5, 5, 5)), min, max)!.Value, 1e-4f);                     // scaled: its face is 5 away
        Assert.Null(Picking.HitBox(ray, new Trs(new Double3(3, 0, 0), Quaternion.Identity, Vector3.One), min, max));                                      // beside it
        float turned = Picking.HitBox(ray, new Trs(Double3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4), Vector3.One), min, max)!.Value;
        Assert.Equal(10f - MathF.Sqrt(2), turned, 1e-3f);                                                                                                 // a corner towards us
        Assert.Null(Picking.HitBox(new PickRay(new Double3(0, 0, -10), new Vector3(0, 0, -1)), new Trs(Double3.Zero, Quaternion.Identity, Vector3.One), min, max));   // behind the ray
        Assert.Equal(0f, Picking.HitBox(new PickRay(Double3.Zero, new Vector3(0, 0, -1)), new Trs(Double3.Zero, Quaternion.Identity, Vector3.One), min, max)!.Value);  // starting inside
    }

    [Fact]
    public void ARayHitsASphereAtItsNearSideAndTheGroundWhereItLands()
    {
        var ray = new PickRay(new Double3(0, 0, 10), new Vector3(0, 0, -1));
        Assert.Equal(9.7f, Picking.HitSphere(ray, Double3.Zero, 0.3f)!.Value, 1e-4f);
        Assert.Null(Picking.HitSphere(ray, new Double3(1, 0, 0), 0.3f));
        Assert.Null(Picking.HitSphere(ray, new Double3(0, 0, 20), 0.3f));        // behind the origin

        var down = new PickRay(new Double3(3, 5, 4), Vector3.Normalize(new Vector3(0, -1, 0)));
        Double3 ground = Picking.HitGround(down)!.Value;
        Assert.True(Double3.Distance(ground, new Double3(3, 0, 4)) < 1e-4);
        Assert.Null(Picking.HitGround(new PickRay(new Double3(0, 5, 0), Vector3.UnitY)));      // looking up
        Assert.Null(Picking.HitGround(new PickRay(new Double3(0, 5, 0), Vector3.UnitX)));      // along the plane
    }
}
