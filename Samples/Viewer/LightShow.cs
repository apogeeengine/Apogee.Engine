using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Content;

namespace Apogee.Samples.Viewer;

/// <summary>Colored point lights that drift around a box of space. Positions are a function of time only, so a given frame always looks the same.</summary>
internal sealed class LightShow
{
    private readonly Light[] _lights;

    private readonly record struct Light(Vector3 Center, Vector3 Radius, float Height, float Bob, float Speed, float Phase, Vector3 Color, float Intensity, float Range);

    public LightShow(int count, Vector3 min, Vector3 max, int seed = 7)
    {
        var random = new Random(seed);
        Vector3 center = (min + max) * 0.5f;
        Vector3 half = (max - min) * 0.5f;
        _lights = new Light[count];
        for (int i = 0; i < count; i++)
        {
            float hue = (float)random.NextDouble();
            _lights[i] = new Light(
                new Vector3(center.X + (float)(random.NextDouble() * 2 - 1) * half.X * 0.8f, 0, center.Z + (float)(random.NextDouble() * 2 - 1) * half.Z * 0.8f),
                new Vector3(0.5f + (float)random.NextDouble() * 2.5f, 0, 0.5f + (float)random.NextDouble() * 2.5f),
                min.Y + 0.8f + (float)random.NextDouble() * Math.Min(3f, half.Y),
                (float)random.NextDouble() * 0.5f,
                0.2f + (float)random.NextDouble() * 0.6f,
                (float)random.NextDouble() * MathF.Tau,
                Hue(hue),
                2.2f + (float)random.NextDouble() * 2.6f,
                3f + (float)random.NextDouble() * 1.5f);
        }
    }

    public int Count => _lights.Length;

    /// <summary>Rewrites <paramref name="target"/> with the lights as they are at <paramref name="seconds"/>.</summary>
    public void Update(float seconds, List<LightData> target)
    {
        target.Clear();
        for (int i = 0; i < _lights.Length; i++)
        {
            Light l = _lights[i];
            float angle = seconds * l.Speed + l.Phase;
            var position = new Vector3(
                l.Center.X + MathF.Cos(angle) * l.Radius.X,
                l.Height + MathF.Sin(angle * 1.7f) * l.Bob,
                l.Center.Z + MathF.Sin(angle * 1.3f) * l.Radius.Z);
            target.Add(new LightData("show", LightKind.Point, l.Color, l.Intensity, l.Range, 0f, 0f, position, -Vector3.UnitY));
        }
    }

    /// <summary>A fully bright, mostly saturated color for a hue in [0, 1).</summary>
    internal static Vector3 Hue(float hue)
    {
        float h = hue * 6f;
        float r = Math.Clamp(MathF.Abs(h - 3f) - 1f, 0f, 1f);
        float g = Math.Clamp(2f - MathF.Abs(h - 2f), 0f, 1f);
        float b = Math.Clamp(2f - MathF.Abs(h - 4f), 0f, 1f);
        return Vector3.Lerp(Vector3.One, new Vector3(r, g, b), 0.75f);
    }
}
