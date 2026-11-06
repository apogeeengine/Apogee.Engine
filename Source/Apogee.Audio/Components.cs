using System.Numerics;
using Apogee.Reflection;

namespace Apogee.Audio;

#pragma warning disable CA1051 // The fields of a component are its memory layout.

/// <summary>
/// Marks the entity the player hears from (the camera, or the character's head): the audio system hears from its <see cref="Ecs.WorldTransform"/>, looking along its -Z with +Y up, as a
/// camera does. Several give split-screen listeners (the lowest <see cref="Index"/> first, up to 8); each voice is placed relative to the nearest one, in double precision, so sound stays
/// exact far from the origin.
/// </summary>
[Component]
public struct AudioListener
{
    public byte Index;
}

/// <summary>
/// A box (centered on the entity, <see cref="HalfExtents"/> in its own space, scaled by its world scale) in which the listener hears <see cref="Preset"/>'s reverb: a garage, a barn, a cave.
/// Where zones overlap, the one with the highest <see cref="Priority"/> wins, then the smallest. Only the zone the listener is in applies; there is no crossfade.
/// </summary>
[Component]
public struct ReverbZone
{
    public Vector3 HalfExtents;

    /// <summary>A <see cref="ReverbPreset"/>.</summary>
    public byte Preset;

    public int Priority;

    /// <summary>Added to the preset's wet level, in decibels: negative for less reverb.</summary>
    public float Level;
}

#pragma warning restore CA1051

/// <summary>The reverbs <see cref="ReverbZone"/> knows. They are our own approximations of the usual spaces, as OpenAL Soft's EAX reverb parameters.</summary>
public enum ReverbPreset : byte
{
    Off = 0,
    Generic = 1,
    Room = 2,
    Hallway = 3,
    Garage = 4,
    Barn = 5,
    Hangar = 6,
    Cave = 7,
    Forest = 8,
    Plain = 9,
}
