using System;
using Apogee.Reflection;

namespace Apogee.Audio;

/// <summary>
/// Something that is playing, or about to: a sound on a source (<see cref="AudioSound.Play"/>). Attach it to an entity to make it positional: it then follows that entity's world transform, with the velocity
/// for Doppler, and is set relative to the nearest listener in double precision. A voice from an inactive audio system (no OpenAL) is inert: it never plays.
/// </summary>
/// <remarks>
/// Letting go of the voice: a sound that does not loop plays on to its end; a looping sound is stopped when its voice is destroyed, which a script's voice is when the script drops it. Keep the voice of
/// something that loops.
/// </remarks>
[ApogeeClass]
public sealed partial class AudioVoice : EngineObject
{
    private readonly Playback? _playback;
    private float _volume = 1;
    private float _pitch = 1;
    private bool _paused;
    private bool _occlusion;
    private Entity _entity;

    /// <summary>An inert voice.</summary>
    internal AudioVoice(AudioSystem system)
    {
        _ = system;
    }

    internal AudioVoice(AudioSystem system, Playback playback)
    {
        _ = system;
        _playback = playback;
        _entity = playback.Entity;
        _volume = playback.Volume;
        _pitch = playback.Pitch;
    }

    /// <summary>The entity the voice follows in 3D, or 0 for a voice that is not positional (or sits at the listener).</summary>
    [Property]
    public Entity Entity
    {
        get => _entity;
        set
        {
            _entity = value;
            if (_playback is not null)
            {
                _playback.Entity = value;
            }
        }
    }

    /// <summary>Check the way from the listener with the physics world's rays and muffle the sound behind walls (smoothly): needs <see cref="AudioSystem.Physics"/> and an <see cref="Entity"/>.</summary>
    [Property]
    public bool Occlusion
    {
        get => _occlusion;
        set
        {
            _occlusion = value;
            if (_playback is not null)
            {
                _playback.Occlusion = value;
            }
        }
    }

    [Property]
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Max(0, value);
            if (_playback is not null)
            {
                _playback.Volume = _volume;
            }
        }
    }

    /// <summary>Playback speed and pitch together: 1 is as recorded, 2 an octave up.</summary>
    [Property]
    public float Pitch
    {
        get => _pitch;
        set
        {
            _pitch = Math.Max(0.01f, value);
            if (_playback is not null)
            {
                _playback.Pitch = _pitch;
            }
        }
    }

    /// <summary>The distance in meters up to which the voice is not made quieter.</summary>
    [Property]
    public float MinDistance
    {
        get => _playback?.MinDistance ?? 1;
        set
        {
            if (_playback is not null)
            {
                _playback.MinDistance = value;
            }
        }
    }

    /// <summary>The distance in meters beyond which the voice gets no quieter.</summary>
    [Property]
    public float MaxDistance
    {
        get => _playback?.MaxDistance ?? 10000;
        set
        {
            if (_playback is not null)
            {
                _playback.MaxDistance = value;
            }
        }
    }

    [Property]
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (_playback is not null)
            {
                _playback.Paused = value;
            }
        }
    }

    /// <summary>Whether it is still sounding (a paused sound is).</summary>
    [Property]
    public bool IsPlaying => _playback?.IsPlaying ?? false;

    /// <summary>The playback position in seconds.</summary>
    [Property]
    public float Position => _playback?.Position ?? 0;

    /// <summary>How much the sound is muffled by what is between it and the listener, 0 (nothing) to 1.</summary>
    [Property]
    public float Muffling => _playback?.Muffling ?? 0;

    /// <summary>Stops it at once.</summary>
    [Method]
    public void Stop() => _playback?.Stop();

    protected override void OnDestroyed() => _playback?.Detach();
}
