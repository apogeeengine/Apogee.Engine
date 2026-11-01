using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using Apogee.Core.Numerics;
using Xunit;

namespace Apogee.Core.Tests;

public class FrustumCullingTests
{
    /// <summary>A camera at the origin looking down -Z, 90 degrees tall, 16:9 (aspect 1.78), near 0.5.</summary>
    private static Frustum Forward() => Frustum.FromCamera(Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ, 1f, 16f / 9f, 0.5f);

    private static Frustum Random(Random random)
    {
        Quaternion turn = Quaternion.CreateFromYawPitchRoll((float)(random.NextDouble() * 6.28), (float)((random.NextDouble() - 0.5) * 3), (float)(random.NextDouble() * 6.28));
        return Frustum.FromCamera(
            Vector3.Transform(Vector3.UnitX, turn),
            Vector3.Transform(Vector3.UnitY, turn),
            Vector3.Transform(-Vector3.UnitZ, turn),
            (float)(0.2 + (random.NextDouble() * 1.5)),
            (float)(0.7 + random.NextDouble()),
            (float)(0.05 + (random.NextDouble() * 2)));
    }

    private static BoxSet RandomBoxes(Random random, int count, float spread)
    {
        var boxes = new BoxSet(count);
        for (int i = 0; i < count; i++)
        {
            var center = new Vector3((float)((random.NextDouble() - 0.5) * spread), (float)((random.NextDouble() - 0.5) * spread), (float)((random.NextDouble() - 0.5) * spread));
            var extents = new Vector3((float)(random.NextDouble() * 4), (float)(random.NextDouble() * 4), (float)(random.NextDouble() * 4));
            boxes.Set(i, center, extents);
        }

        return boxes;
    }

    [Fact]
    public void ABoxInFrontIsInsideAndOneBehindOrFarToTheSideIsOutside()
    {
        Frustum frustum = Forward();

        Assert.True(frustum.Intersects(new Vector3(0, 0, -10), new Vector3(1, 1, 1)));
        Assert.True(frustum.Intersects(new Vector3(0, 0, -1e6f), Vector3.One));       // the far plane is at infinity
        Assert.False(frustum.Intersects(new Vector3(0, 0, 10), new Vector3(1, 1, 1))); // behind
        Assert.False(frustum.Intersects(new Vector3(0, 0, -0.1f), new Vector3(0.1f))); // in front of the near plane
        Assert.False(frustum.Intersects(new Vector3(100, 0, -10), Vector3.One));       // far to the right
        Assert.False(frustum.Intersects(new Vector3(0, -100, -10), Vector3.One));      // far below
    }

    [Fact]
    public void ABoxThatStraddlesAPlaneIsInside()
    {
        Frustum frustum = Forward();

        // At 10 m ahead the view is 10 m tall (tan 45 degrees = 1) and 17.8 m wide on each side of the axis.
        Assert.True(frustum.Intersects(new Vector3(0, 10.5f, -10), new Vector3(0.1f, 1f, 0.1f)));   // pokes into the top edge
        Assert.False(frustum.Intersects(new Vector3(0, 12f, -10), new Vector3(0.1f, 1f, 0.1f)));    // just above it
        Assert.True(frustum.Intersects(new Vector3(0, 0, -0.4f), new Vector3(0.1f, 0.1f, 0.2f)));   // reaches across the near plane
    }

    [Fact]
    public void ABoxOfNoSizeIsATestOfItsCenter()
    {
        Frustum frustum = Forward();

        Assert.True(frustum.Intersects(new Vector3(0, 0, -5), Vector3.Zero));
        Assert.False(frustum.Intersects(new Vector3(0, 0, 5), Vector3.Zero));
    }

    [Fact]
    public void AnOffsetFrustumTestsBoxesRelativeToAnotherOrigin()
    {
        Frustum frustum = Forward();
        var origin = new Vector3(1000, 0, 0);               // boxes are given relative to a point 1 km to the right of the camera

        Frustum shifted = frustum.Offset(origin);

        Assert.False(shifted.Intersects(new Vector3(0, 0, -10), Vector3.One));        // that point's own -10 m ahead is far to the right of the camera
        Assert.True(shifted.Intersects(new Vector3(-1000, 0, -10), Vector3.One));     // this one is straight ahead of the camera
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(1000)]
    [InlineData(4099)]
    public void EveryKernelGivesTheSameBitsAsTheScalarOne(int count)
    {
        var random = new Random(count + 17);
        for (int round = 0; round < 8; round++)
        {
            Frustum frustum = Random(random);
            BoxSet boxes = RandomBoxes(random, count, 60f);
            byte[] scalar = new byte[boxes.ResultLength];
            byte[] narrow = new byte[boxes.ResultLength];
            byte[] wide = new byte[boxes.ResultLength];
            byte[] chosen = new byte[boxes.ResultLength];

            FrustumCulling.CullScalar(frustum, boxes, scalar);
            FrustumCulling.CullVector128(frustum, boxes, narrow);
            FrustumCulling.Cull(frustum, boxes, chosen);
            Assert.Equal(CountOf(scalar, count), CountOf(narrow, count));
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(FrustumCulling.IsVisible(scalar, i), FrustumCulling.IsVisible(narrow, i));
                Assert.Equal(FrustumCulling.IsVisible(scalar, i), FrustumCulling.IsVisible(chosen, i));
            }

            if (Vector256.IsHardwareAccelerated)
            {
                FrustumCulling.CullVector256(frustum, boxes, wide);
                for (int i = 0; i < count; i++)
                {
                    Assert.Equal(FrustumCulling.IsVisible(scalar, i), FrustumCulling.IsVisible(wide, i));
                }
            }
        }
    }

    [Fact]
    public void ARandomSceneHasBothVisibleAndCulledBoxes()
    {
        var random = new Random(3);
        BoxSet boxes = RandomBoxes(random, 5000, 200f);
        byte[] bits = new byte[boxes.ResultLength];

        FrustumCulling.Cull(Forward(), boxes, bits);

        int visible = CountOf(bits, boxes.Count);
        Assert.InRange(visible, 100, 4000);
    }

    [Fact]
    public void NotANumberIsNeverCulledAndAPaddingBoxDoesNotCount()
    {
        BoxSet boxes = new(3);
        boxes.Set(0, new Vector3(float.NaN, 0, -5), Vector3.One);
        boxes.Set(1, new Vector3(0, 0, 50), Vector3.One);
        boxes.Set(2, new Vector3(0, 0, -50), Vector3.One);
        byte[] scalar = new byte[boxes.ResultLength];
        byte[] vector = new byte[boxes.ResultLength];

        FrustumCulling.CullScalar(Forward(), boxes, scalar);
        FrustumCulling.CullVector128(Forward(), boxes, vector);

        Assert.Equal([true, false, true], [FrustumCulling.IsVisible(scalar, 0), FrustumCulling.IsVisible(scalar, 1), FrustumCulling.IsVisible(scalar, 2)]);
        Assert.Equal(scalar[0] & 0b111, vector[0] & 0b111);
        Assert.Equal(1, boxes.ResultLength);
        Assert.Equal(8, boxes.PaddedCount);
    }

    [Fact]
    public void ATooShortResultIsRefused()
    {
        BoxSet boxes = new(20);

        Assert.Throws<ArgumentException>(() => FrustumCulling.CullScalar(Forward(), boxes, new byte[2]));
    }

    private static int CountOf(byte[] bits, int count) => FrustumCulling.CountVisible(bits, count);
}
