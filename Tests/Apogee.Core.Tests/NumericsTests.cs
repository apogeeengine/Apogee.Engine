using System;
using System.Numerics;
using Apogee.Core.Numerics;
using Xunit;

namespace Apogee.Core.Tests;

public class NumericsTests
{
    [Fact]
    public void Double3IsTwentyFourBytes() => Assert.Equal(24, System.Runtime.CompilerServices.Unsafe.SizeOf<Double3>());

    [Fact]
    public void Double3Arithmetic()
    {
        var a = new Double3(1, 2, 3);
        var b = new Double3(4, 6, 8);

        Assert.Equal(new Double3(5, 8, 11), a + b);
        Assert.Equal(new Double3(3, 4, 5), b - a);
        Assert.Equal(new Double3(2, 4, 6), a * 2);
        Assert.Equal(new Double3(-1, -2, -3), -a);
        Assert.Equal(40, Double3.Dot(a, b));
        Assert.Equal(5, Double3.Distance(a, new Double3(1, 6, 6)));
        Assert.Equal(new Double3(2.5, 4, 5.5), Double3.Lerp(a, b, 0.5));
        Assert.Equal(new Double3(1, 2, 3), Double3.Min(a, b));
        Assert.Equal(new Double3(4, 6, 8), Double3.Max(a, b));
    }

    [Fact]
    public void AFloatPositionLosesPrecisionFarAwayButARelativeOffsetDoesNot()
    {
        // At 5.8 km (the corner of an 8 km map) a float cannot tell 0.3 mm apart; 10 000 km is far worse.
        var far = new Double3(10_000_000.0, 0, -10_000_000.0);
        var camera = far + new Vector3(0.001f, 0, 0);
        var point = far + new Vector3(2.5f, 1, -3);

        Assert.Equal(far.X, (double)(float)far.X); // 10 000 000 happens to be exact in a float...
        Assert.NotEqual(camera.X, (double)(float)camera.X); // ...but the millimetre next to it is not.

        Vector3 relative = point.RelativeTo(camera);
        Assert.Equal(2.499f, relative.X, 1e-4f);
        Assert.Equal(1f, relative.Y, 1e-5f);
        Assert.Equal(-3f, relative.Z, 1e-5f);
    }

    [Fact]
    public void ARootPlacementTransformsPointsAndInvertsThem()
    {
        var placement = new Trs(new Double3(1_000_000, 5, -2_000_000), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), new Vector3(2, 1, 3));
        var local = new Vector3(1, 2, 3);

        Double3 world = placement.TransformPoint(local);
        Vector3 back = placement.InverseTransformPoint(world);

        // Scale (2,1,3) → (2,2,9); a quarter turn about +Y sends (x, y, z) to (z, y, -x).
        Assert.Equal(1_000_009.0, world.X, 1e-4);
        Assert.Equal(7.0, world.Y, 1e-4);
        Assert.Equal(-2_000_002.0, world.Z, 1e-4);
        Assert.Equal(local.X, back.X, 1e-4f);
        Assert.Equal(local.Y, back.Y, 1e-4f);
        Assert.Equal(local.Z, back.Z, 1e-4f);
    }

    [Fact]
    public void AChildInWorldOfItsParentLandsWhereTheTwoTransformsPutIt()
    {
        var parent = new Trs(new Double3(100, 0, -50), Quaternion.CreateFromYawPitchRoll(0.7f, 0.2f, -0.4f), new Vector3(2));
        var child = new Trs(new Double3(1, 2, 3), Quaternion.CreateFromYawPitchRoll(-0.3f, 0.5f, 0.1f), new Vector3(1.5f));
        var point = new Vector3(0.5f, -1, 2);

        Double3 viaChain = parent.TransformPoint((Vector3)child.TransformPoint(point));
        Double3 viaWorld = child.InWorldOf(parent).TransformPoint(point);

        Assert.True(Double3.Distance(viaChain, viaWorld) < 1e-4, $"{viaChain} vs {viaWorld}");
    }

    [Fact]
    public void AWorldPlacementIsLocalInsideAParentAndComesBackToTheSameWorldPlacement()
    {
        var parent = new Trs(new Double3(100, 0, -50), Quaternion.CreateFromYawPitchRoll(0.7f, 0.2f, -0.4f), new Vector3(2));
        var world = new Trs(new Double3(103, 5, -49), Quaternion.CreateFromYawPitchRoll(-0.3f, 0.5f, 0.1f), new Vector3(3));

        Trs local = world.InLocalOf(parent);
        Trs again = local.InWorldOf(parent);

        Assert.True(Double3.Distance(again.Position, world.Position) < 1e-4, $"{again.Position} vs {world.Position}");
        Assert.True(Math.Abs(Quaternion.Dot(again.Rotation, world.Rotation)) > 0.99999f);
        Assert.Equal(3f, again.Scale.X, 1e-5f);
        Assert.Equal(1.5f, local.Scale.X, 1e-5f);
    }

    [Fact]
    public void ANonUniformParentScaleWithoutRotationComposesExactly()
    {
        var parent = new Trs(new Double3(10, 0, 0), Quaternion.Identity, new Vector3(2, 1, 4));
        var child = new Trs(new Double3(1, 1, 1), Quaternion.Identity, new Vector3(3, 3, 3));

        Trs world = child.InWorldOf(parent);

        Assert.Equal(new Double3(12, 1, 4), world.Position);
        Assert.Equal(new Vector3(6, 3, 12), world.Scale);
    }

    [Fact]
    public void TheMatrixIsRelativeToTheOriginAndMatchesTheTransform()
    {
        var placement = new Trs(new Double3(5_000_000.25, 3, -5_000_000.5), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f), new Vector3(1, 2, 1));
        var camera = new Double3(5_000_000, 0, -5_000_000);
        var local = new Vector3(0.25f, 1, -1);

        Matrix4x4 matrix = placement.ToMatrix(camera);
        Vector3 viaMatrix = Vector3.Transform(local, matrix);
        Vector3 viaPlacement = placement.TransformPoint(local).RelativeTo(camera);

        Assert.Equal(viaPlacement.X, viaMatrix.X, 1e-4f);
        Assert.Equal(viaPlacement.Y, viaMatrix.Y, 1e-4f);
        Assert.Equal(viaPlacement.Z, viaMatrix.Z, 1e-4f);
    }
}
