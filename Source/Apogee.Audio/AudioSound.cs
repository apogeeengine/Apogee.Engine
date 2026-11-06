using System;
using Apogee.Native.OpenAL;
using Apogee.Reflection;
using AL = Apogee.Native.OpenAL.OpenAL;

namespace Apogee.Audio;

/// <summary>A sound file loaded by <see cref="AudioSystem.LoadSound"/>: play it any number of times, on any bus.</summary>
[ApogeeClass]
public sealed unsafe partial class AudioSound : EngineObject
{
    private readonly AudioSystem _system;
    private uint _buffer;

    internal AudioSound(AudioSystem system, string path, bool is3D, bool isStream, uint buffer, PinnedBytes? pinned, int channels, int sampleRate, long frames)
    {
        _system = system;
        Path = path;
        Is3D = is3D;
        IsStream = isStream;
        _buffer = buffer;
        Pinned = pinned;
        Channels = channels;
        SampleRate = sampleRate;
        Frames = frames;
        Adopt();
    }

    [Property]
    public string Path { get; }

    [Property]
    public bool Is3D { get; }

    /// <summary>The distance in meters up to which the sound is not made quieter (inverse-distance rolloff starts here): 1. Voices of this sound start with it.</summary>
    [Property]
    public float MinDistance { get; set; } = 1;

    /// <summary>The distance in meters beyond which the sound gets no quieter: 10000. Voices of this sound start with it.</summary>
    [Property]
    public float MaxDistance { get; set; } = 10000;

    internal bool IsStream { get; }

    internal uint Buffer => _buffer;

    /// <summary>The file, kept for a stream so that each voice can open its own decoder on it.</summary>
    internal PinnedBytes? Pinned { get; private set; }

    internal int Channels { get; }

    internal int SampleRate { get; }

    internal long Frames { get; }

    internal bool LoopsByDefault { get; set; }

    /// <summary>The length in seconds (0 when the file does not say).</summary>
    [Property]
    public float Length => SampleRate == 0 ? 0 : Frames / (float)SampleRate;

    /// <summary>
    /// Plays it on <paramref name="bus"/> (see <see cref="AudioSystem.GetBus"/>; empty is the master mix), at this volume and pitch, looping if asked (or if it was loaded with <c>loop</c>),
    /// following <paramref name="entity"/> if given. Returns the voice.
    /// </summary>
    [Method]
    public AudioVoice Play(string bus = "", float volume = 1, float pitch = 1, bool loop = false, Entity entity = default)
    {
        if (!_system.IsActive || (_buffer == 0 && Pinned is null))
        {
            return new AudioVoice(_system);
        }

        Playback? playback = Playback.Start(_system, this, _system.GetBus(bus), volume, pitch, loop || LoopsByDefault, entity);
        return playback is null ? new AudioVoice(_system) : new AudioVoice(_system, playback);
    }

    protected override void OnDestroyed()
    {
        _system.StopSound(this); // OpenAL does not delete a buffer that a source still has
        if (_buffer != 0)
        {
            uint buffer = _buffer;
            _buffer = 0;
            AL.AlDeleteBuffers(1, &buffer);
        }

        Pinned?.Dispose();
        Pinned = null;
    }
}
