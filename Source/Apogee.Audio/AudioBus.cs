using System;
using Apogee.Reflection;

namespace Apogee.Audio;

/// <summary>A bus of the mix: a group of sounds with a volume, a mute and a pause of its own (Music, Vehicles, Environment, UI). See <see cref="AudioSystem.GetBus"/>.</summary>
[ApogeeClass]
public sealed partial class AudioBus : EngineObject
{
    private readonly AudioSystem _system;
    private float _volume = 1;
    private bool _muted;

    internal AudioBus(AudioSystem system, string name)
    {
        _system = system;
        Name = name;
    }

    [Property]
    public string Name { get; }

    /// <summary>The gain the bus gives its sounds: its volume, or nothing when it is muted. The master bus's volume is the listener's gain (<see cref="AudioSystem.Volume"/>), so here it only mutes.</summary>
    internal float EffectiveGain => _muted ? 0 : IsMaster ? 1 : _volume;

    internal bool IsMaster => Name == "master";

    internal bool Paused { get; private set; }

    /// <summary>The volume, 0 to 1 and above. The volume of the <c>master</c> bus is <see cref="AudioSystem.Volume"/>.</summary>
    [Property]
    public float Volume
    {
        get => IsMaster ? _system.Volume : _volume;
        set
        {
            if (IsMaster)
            {
                _system.Volume = value;
                return;
            }

            _volume = Math.Max(0, value);
            _system.BusChanged(this);
        }
    }

    [Property]
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            _system.BusChanged(this);
        }
    }

    /// <summary>Pauses everything on the bus, and resumes it.</summary>
    [Method]
    public void SetPaused(bool paused)
    {
        Paused = paused;
        _system.BusChanged(this);
    }

    /// <summary>Stops everything on the bus.</summary>
    [Method]
    public void StopAll() => _system.StopBus(this);
}
