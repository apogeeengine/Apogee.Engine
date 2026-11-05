using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apogee.Core.Graphics;
using Apogee.Core.Numerics;
using Apogee.Native.Jolt;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Physics.Tests;

public class DebugDrawTests : PhysicsFixture
{
    [Fact]
    public void TheDebugStructsHaveTheLayoutOfJoltsSoTheyAreCopiedAsTheyAre()
    {
        Assert.Equal(Unsafe.SizeOf<AjDebugLine>(), Unsafe.SizeOf<DebugLine>());
        Assert.Equal(Unsafe.SizeOf<AjDebugTriangle>(), Unsafe.SizeOf<DebugTriangle>());
        Assert.Equal(56, Unsafe.SizeOf<DebugLine>());
        Assert.Equal(80, Unsafe.SizeOf<DebugTriangle>());
        Assert.Equal(24, (int)Marshal.OffsetOf<AjDebugLine>(nameof(AjDebugLine.To)));
        Assert.Equal(48, (int)Marshal.OffsetOf<AjDebugLine>(nameof(AjDebugLine.Color)));
        Assert.Equal(48, (int)Marshal.OffsetOf<AjDebugTriangle>(nameof(AjDebugTriangle.C)));
        Assert.Equal(72, (int)Marshal.OffsetOf<AjDebugTriangle>(nameof(AjDebugTriangle.Color)));
    }

    [Fact]
    public void AWireframeBoxIsItsMeshsEdgesAtItsPlaceInTheWorld()
    {
        Entity box = Spawn(new Double3(1_000_000, 5, -2_000_000));
        Physics.AddBody(box, new BodyDesc { Shape = PhysicsShape.Box(new Vector3(1, 2, 3), convexRadius: 0), Motion = MotionKind.Static });
        var draw = new DebugDraw();

        Physics.DrawDebug(draw, new PhysicsDebugOptions { Constraints = false, Characters = false });

        Assert.Equal(36, draw.LineCount); // the cube's mesh: twelve triangles, three edges each
        Assert.Equal(0, draw.TriangleCount);

        // Every line starts and ends on a corner of the box, in double precision: a million meters out and exact.
        foreach (DebugLine line in draw.Lines)
        {
            foreach (Double3 end in new[] { line.From, line.To })
            {
                Assert.Equal(1.0, Math.Abs(end.X - 1_000_000), 1e-4);
                Assert.Equal(2.0, Math.Abs(end.Y - 5), 1e-4);
                Assert.Equal(3.0, Math.Abs(end.Z + 2_000_000), 1e-4);
            }
        }
    }

    [Fact]
    public void SolidShapesAreTrianglesAndTheOptionsChooseWhatIsDrawn()
    {
        AddGround();
        AddBox(new Double3(0, 3, 0));
        var draw = new DebugDraw();

        Physics.DrawDebug(draw, new PhysicsDebugOptions { Wireframe = false, Constraints = false });
        Assert.True(draw.TriangleCount >= 24, $"{draw.TriangleCount} triangles for two boxes"); // a box is twelve triangles, and the ground's is another box
        Assert.Equal(0, draw.LineCount);

        draw.Clear();
        Physics.DrawDebug(draw, new PhysicsDebugOptions { Shapes = false, Constraints = false, BoundingBoxes = true });
        Assert.True(draw.LineCount >= 24, $"{draw.LineCount} lines for two bounding boxes");
        Assert.Equal(0, draw.TriangleCount);

        draw.Clear();
        Physics.DrawDebug(draw, new PhysicsDebugOptions { Shapes = false, Constraints = false });
        Assert.True(draw.IsEmpty);
    }

    [Fact]
    public void BodiesAreColoredByWhatTheyAreAndACharacterHasACapsule()
    {
        AddGround();
        Entity person = Spawn(new Double3(3, 0, 0));
        Physics.CreateCharacter(person, 1.8f, 0.4f);
        var draw = new DebugDraw();

        Physics.DrawDebug(draw, new PhysicsDebugOptions { Shapes = false, Constraints = false, Characters = true });

        Assert.True(draw.LineCount > 20, "a capsule is many lines");
        Assert.All(draw.Lines.ToArray(), line =>
        {
            Assert.Equal(0u, line.Color & 0x00FF00FFu);          // no red, no blue: the character's pure green
            Assert.True(((line.Color >> 8) & 0xFF) > 200, "and a lot of it");
            Assert.InRange(line.From.X, 2.5, 3.5);      // around where the character stands
            Assert.InRange(line.From.Y, -0.05, 1.95);
        });

        draw.Clear();
        Physics.DrawDebug(draw, new PhysicsDebugOptions { Shapes = false, Constraints = false, Characters = false });
        Assert.True(draw.IsEmpty);
    }

    [Fact]
    public void DrawingAgainAddsAndDoesNotRepeatTheEarlierPicture()
    {
        AddGround();
        var draw = new DebugDraw();

        Physics.DrawDebug(draw, new PhysicsDebugOptions { Constraints = false });
        int once = draw.LineCount;
        Physics.DrawDebug(draw, new PhysicsDebugOptions { Constraints = false });

        Assert.True(once > 0);
        Assert.Equal(once * 2, draw.LineCount); // each call adds the world once: Jolt's own collector was emptied in between
    }
}

public class DebugDrawShapesTests
{
    [Fact]
    public void TheShapeHelpersMakeTheLinesTheyShould()
    {
        var draw = new DebugDraw();

        draw.Box(Double3.Zero, new Vector3(1), Quaternion.Identity, DebugColor.Red);
        Assert.Equal(12, draw.LineCount);

        draw.Clear();
        draw.Sphere(new Double3(5, 5, 5), 2f, DebugColor.Blue, segments: 16);
        Assert.Equal(48, draw.LineCount); // three circles of sixteen
        Assert.All(draw.Lines.ToArray(), line => Assert.Equal(2.0, (line.From - new Double3(5, 5, 5)).Length(), 1e-6));

        draw.Clear();
        draw.Arrow(Double3.Zero, new Double3(0, 0, 10), DebugColor.Yellow);
        Assert.Equal(5, draw.LineCount); // the shaft and four lines of the head

        draw.Clear();
        draw.Axes(Double3.Zero, Quaternion.Identity, 2f);
        Assert.Equal([DebugColor.Red, DebugColor.Green, DebugColor.Blue], draw.Lines.ToArray().Select(l => l.Color));
    }

    [Fact]
    public void WhatDoesNotFitIsCountedAndLeftOut()
    {
        var draw = new DebugDraw { Capacity = 10 };

        for (int i = 0; i < 15; i++)
        {
            draw.Line(Double3.Zero, new Double3(i, 0, 0), DebugColor.White);
        }

        draw.AddLines([new DebugLine(Double3.Zero, Double3.Zero, 0), new DebugLine(Double3.Zero, Double3.Zero, 0)]);

        Assert.Equal(10, draw.LineCount);
        Assert.Equal(7, draw.Dropped);
        draw.Clear();
        Assert.Equal(0, draw.Dropped);
        Assert.True(draw.IsEmpty);
    }

    [Fact]
    public void ColorsPackAsBytesRgba()
    {
        Assert.Equal(0xFF332211u, DebugColor.Rgba(0x11, 0x22, 0x33));
        Assert.Equal(0x80000000u | 0xFFu, DebugColor.FromVector(new Vector4(1f, 0f, 0f, 0.5f)));
    }
}
