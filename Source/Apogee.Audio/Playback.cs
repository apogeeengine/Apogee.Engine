using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.OpenAL;
using Apogee.Physics;
using Apogee.Reflection;
using AL = Apogee.Native.OpenAL.OpenAL;

namespace Apogee.Audio;

/// <summary>A sound being decoded in pieces while it plays: a queue of OpenAL buffers that is topped up as the source finishes them.</summary>
internal sealed unsafe class AudioStream : IDisposable
{
    private const int BufferCount = 4;

    private readonly SoundDecoder _decoder;
    private readonly uint[] _buffers = new uint[BufferCount];
    private readonly Dictionary<uint, int> _frames = [];
    private readonly float[] _chunk;
    private readonly int _channels;
    private readonly int _rate;
    private readonly bool _loop;
    private int _freeBuffers;

    private AudioStream(SoundDecoder decoder, bool loop)
    {
        _decoder = decoder;
        _loop = loop;
        _channels = decoder.Channels;
        _rate = decoder.SampleRate;
        _chunk = new float[_rate / 4 * _channels]; // a quarter of a second a buffer
        fixed (uint* buffers = _buffers)
        {
            AL.AlGenBuffers(BufferCount, buffers);
        }
    }

    /// <summary>The frames of the buffers the source has finished, to add to its offset in the queue.</summary>
    public long FramesDone { get; private set; }

    /// <summary>The decoder has nothing more (and the stream does not loop).</summary>
    public bool Ended { get; private set; }

    public static AudioStream? Open(AudioSound sound, bool loop)
    {
        SoundDecoder? decoder = sound.Pinned is null ? null : SoundDecoder.Open(sound.Pinned);
        return decoder is null ? null : new AudioStream(decoder, loop);
    }

    /// <summary>Fills the first buffers and queues them on the source.</summary>
    public void Prime(uint source)
    {
        foreach (uint buffer in _buffers)
        {
            if (!Refill(buffer, source))
            {
                break;
            }
        }
    }

    /// <summary>Takes back the buffers the source has played and fills them again.</summary>
    public void Service(uint source)
    {
        int processed;
        AL.AlGetSourcei(source, AL.AlBuffersProcessed, &processed);
        while (processed-- > 0)
        {
            uint buffer;
            AL.AlSourceUnqueueBuffers(source, 1, &buffer);
            FramesDone += _frames.GetValueOrDefault(buffer);
            _frames[buffer] = 0;
            _freeBuffers++;
            if (!Ended)
            {
                Refill(buffer, source);
            }
        }
    }

    private bool Refill(uint buffer, uint source)
    {
        int frames = _decoder.Read(_chunk, _rate / 4);
        if (frames < _rate / 4)
        {
            if (_loop && _decoder.Rewind())
            {
                int more = _decoder.Read(_chunk.AsSpan(frames * _channels), (_rate / 4) - frames);
                frames += Math.Max(0, more);
                if (more <= 0 && frames == 0)
                {
                    Ended = true;
                }
            }
            else
            {
                Ended = true;
            }
        }

        if (frames <= 0)
        {
            return false;
        }

        fixed (float* data = _chunk)
        {
            AL.AlBufferData(buffer, AudioSystem.FormatOf(_channels), data, frames * _channels * sizeof(float), _rate);
        }

        _frames[buffer] = frames;
        AL.AlSourceQueueBuffers(source, 1, &buffer);
        _freeBuffers--;
        return true;
    }

    public void Dispose()
    {
        _decoder.Dispose();
        fixed (uint* buffers = _buffers)
        {
            AL.AlDeleteBuffers(BufferCount, buffers);
        }
    }
}

/// <summary>
/// One sound playing on an OpenAL source, for as long as it lasts. The system tracks it from the <see cref="AudioSound.Play"/> that made it until it is over, whether or not the script still holds its
/// <see cref="AudioVoice"/>: a sound that does not loop plays on to its end, and the source is given back then.
/// </summary>
internal sealed unsafe class Playback
{
    private const float MaxRelativeSpeed = 300; // under the speed of sound: a teleport must not shift the pitch to nonsense

    private readonly AudioSystem _system;
    private readonly AudioSound _sound;
    private uint _source;
    private uint _filter;
    private bool _filterApplied;
    private AudioStream? _stream;
    private bool _finished;
    private bool _detached;
    private bool _haveLast;
    private Double3 _lastPosition;
    private float _volume = 1;
    private float _pitch = 1;
    private float _minDistance;
    private float _maxDistance;
    private bool _paused;

    private Playback(AudioSystem system, AudioSound sound, AudioBus bus, bool looping)
    {
        _system = system;
        _sound = sound;
        Bus = bus;
        Looping = looping;
        _minDistance = sound.MinDistance;
        _maxDistance = sound.MaxDistance;
    }

    public AudioBus Bus { get; }

    public AudioSound Sound => _sound;

    public bool Looping { get; }

    public Entity Entity { get; set; }

    public bool Occlusion { get; set; }

    /// <summary>How much the sound is muffled by what is between it and the listener, 0 (nothing) to 1.</summary>
    public float Muffling { get; private set; }

    public static Playback? Start(AudioSystem system, AudioSound sound, AudioBus bus, float volume, float pitch, bool loop, Entity entity)
    {
        var playback = new Playback(system, sound, bus, loop) { Entity = entity, _volume = Math.Max(0, volume), _pitch = Math.Max(0.01f, pitch) };
        uint source;
        AL.AlGenSources(1, &source);
        if (!AudioSystem.Ok("make a source"))
        {
            return null;
        }

        playback._source = source;
        AL.AlSourcef(source, AL.AlMaxGain, 16);
        AL.AlSourcei(source, AL.AlSourceRelative, sound.Is3D ? 0 : 1);
        AL.AlSourcef(source, AL.AlRolloffFactor, sound.Is3D ? system.Options.RolloffScale : 0);
        playback.ApplyDistances();
        if (system.HasEfx)
        {
            AL.AlSource3i(source, AL.AlAuxiliarySendFilter, (int)system.ReverbSlot, 0, AL.AlFilterNull);
        }

        if (sound.IsStream)
        {
            playback._stream = AudioStream.Open(sound, loop);
            if (playback._stream is null)
            {
                playback.Release();
                return null;
            }

            playback._stream.Prime(source);
        }
        else
        {
            AL.AlSourcei(source, AL.AlBuffer, (int)sound.Buffer);
            AL.AlSourcei(source, AL.AlLooping, loop ? 1 : 0);
        }

        AL.AlSourcef(source, AL.AlPitch, playback._pitch);
        playback.Place(0); // positioned before it is heard
        playback.Apply();
        if (!AudioSystem.Ok("start a sound"))
        {
            playback.Release();
            return null;
        }

        system.Track(playback);
        return playback;
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Max(0, value);
            Apply();
        }
    }

    public float Pitch
    {
        get => _pitch;
        set
        {
            _pitch = Math.Max(0.01f, value);
            if (_source != 0)
            {
                AL.AlSourcef(_source, AL.AlPitch, _pitch);
            }
        }
    }

    public float MinDistance
    {
        get => _minDistance;
        set
        {
            _minDistance = value;
            ApplyDistances();
        }
    }

    public float MaxDistance
    {
        get => _maxDistance;
        set
        {
            _maxDistance = value;
            ApplyDistances();
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Apply();
        }
    }

    /// <summary>Whether it is still sounding (a paused sound is).</summary>
    public bool IsPlaying => !Poll();

    /// <summary>The playback position in seconds.</summary>
    public float Position
    {
        get
        {
            if (_source == 0)
            {
                return 0;
            }

            if (_stream is null)
            {
                float seconds;
                AL.AlGetSourcef(_source, AL.AlSecOffset, &seconds);
                return seconds;
            }

            int offset;
            AL.AlGetSourcei(_source, AL.AlSampleOffset, &offset);
            long frames = _stream.FramesDone + offset;
            if (Looping && _sound.Frames > 0)
            {
                frames %= _sound.Frames;
            }

            return frames / (float)_sound.SampleRate;
        }
    }

    private void ApplyDistances()
    {
        if (_source == 0)
        {
            return;
        }

        AL.AlSourcef(_source, AL.AlReferenceDistance, Math.Max(_minDistance, 1e-3f));
        AL.AlSourcef(_source, AL.AlMaxDistance, Math.Max(_maxDistance, _minDistance));
    }

    private int State()
    {
        int state;
        AL.AlGetSourcei(_source, AL.AlSourceState, &state);
        return state;
    }

    /// <summary>The gain from the voice and its bus, and playing or pausing as the voice and the bus say.</summary>
    public void Apply()
    {
        if (_source == 0 || _finished)
        {
            return;
        }

        AudioBus master = _system.GetBus("master");
        AL.AlSourcef(_source, AL.AlGain, _volume * Bus.EffectiveGain * (Bus == master ? 1 : master.EffectiveGain));
        bool shouldPlay = !_paused && !Bus.Paused && !master.Paused;
        int state = State();
        if (shouldPlay && (state == AL.AlPaused || state == AL.AlInitial))
        {
            AL.AlSourcePlay(_source);
        }
        else if (!shouldPlay && state == AL.AlPlaying)
        {
            AL.AlSourcePause(_source);
        }
    }

    /// <summary>Tops up a stream's buffers, and starts it again if it ran dry before the next block.</summary>
    public void Pump()
    {
        if (_source == 0 || _stream is null || _finished)
        {
            return;
        }

        _stream.Service(_source);
        if (!_stream.Ended && State() == AL.AlStopped && !_paused && !Bus.Paused && !_system.GetBus("master").Paused)
        {
            int queued;
            AL.AlGetSourcei(_source, AL.AlBuffersQueued, &queued);
            if (queued > 0)
            {
                AL.AlSourcePlay(_source);
            }
        }
    }

    /// <summary>Whether it is over; ends it (gives the source back) when it is.</summary>
    private bool Poll()
    {
        if (_finished)
        {
            return true;
        }

        if (_source == 0)
        {
            _finished = true;
            return true;
        }

        if (State() == AL.AlStopped && (_stream is null || _stream.Ended))
        {
            Release();
            return true;
        }

        return false;
    }

    /// <summary>Each frame: places the sound, applies the gain and state, and gives the source back when it is over. False when it no longer needs updates.</summary>
    public bool Refresh(float dt)
    {
        if (_finished)
        {
            return false;
        }

        if (!_detached)
        {
            Place(dt);
        }

        Apply();
        Pump();
        return !Poll();
    }

    private void Place(float dt)
    {
        Entity entity = Entity;
        if (!_sound.Is3D || entity.Id == 0 || !_system.Ecs.IsAlive(entity) || !_system.Ecs.TryGet(entity, out WorldTransform transform))
        {
            return;
        }

        // Relative to the nearest listener and in its frame (the OpenAL listener is at the origin, looking along -Z), with the velocity the Doppler effect needs.
        AudioSystem.ListenerState listener = _system.NearestListener(transform.Position);
        Quaternion inverse = Quaternion.Inverse(listener.Rotation);
        Vector3 offset = (Vector3)(transform.Position - listener.Position);
        Vector3 position = Vector3.Transform(offset, inverse);
        Vector3 velocity = _haveLast && dt > 1e-6f ? (Vector3)(transform.Position - _lastPosition) / dt : Vector3.Zero;
        _lastPosition = transform.Position;
        _haveLast = true;
        Vector3 relative = Vector3.Transform(velocity - listener.Velocity, inverse);
        if (relative.Length() > MaxRelativeSpeed)
        {
            relative = Vector3.Normalize(relative) * MaxRelativeSpeed;
        }

        AL.AlSource3f(_source, AL.AlPosition, position.X, position.Y, position.Z);
        AL.AlSource3f(_source, AL.AlVelocity, relative.X, relative.Y, relative.Z);

        if (Occlusion && _system.PhysicsWorld is { } physics && _system.HasListener)
        {
            float distance = offset.Length();
            float target = 0;
            if (distance > 0.5f)
            {
                // The way from the listener to the sound: a hit that is not at the sound itself blocks it.
                (bool hit, Entity _, Double3 _, Vector3 _, float hitDistance) = physics.Raycast(listener.Position, offset, distance, PhysicsLayers.All, Entity);
                target = hit && hitDistance < distance - 0.1f ? 1 : 0;
            }

            Muffling += (target - Muffling) * Math.Min(1, dt * 8);
            ApplyOcclusion();
        }
        else if (Muffling != 0)
        {
            Muffling = 0;
            ApplyOcclusion();
        }
    }

    /// <summary>A low-pass filter on the direct sound and the reverb send: quieter and duller the more it is muffled.</summary>
    private void ApplyOcclusion()
    {
        if (!_system.HasEfx)
        {
            return;
        }

        if (Muffling < 0.002f)
        {
            if (_filterApplied)
            {
                AL.AlSourcei(_source, AL.AlDirectFilter, AL.AlFilterNull);
                AL.AlSource3i(_source, AL.AlAuxiliarySendFilter, (int)_system.ReverbSlot, 0, AL.AlFilterNull);
                _filterApplied = false;
            }

            return;
        }

        if (_filter == 0)
        {
            _filter = _system.MakeFilter();
            if (_filter == 0)
            {
                return;
            }
        }

        AL.AlFilterf(_filter, AL.AlLowpassGain, 1 - (0.5f * Muffling));
        AL.AlFilterf(_filter, AL.AlLowpassGainhf, 1 - (0.95f * Muffling));
        AL.AlSourcei(_source, AL.AlDirectFilter, (int)_filter); // a filter's values are copied when it is set on the source: again each time
        AL.AlSource3i(_source, AL.AlAuxiliarySendFilter, (int)_system.ReverbSlot, 0, (int)_filter);
        _filterApplied = true;
    }

    public void Stop()
    {
        if (!_finished)
        {
            Release();
        }
    }

    /// <summary>The voice is gone: a loop is stopped (nobody could), anything else plays out on its own and stops following its entity.</summary>
    public void Detach()
    {
        _detached = true;
        if (Looping)
        {
            Stop();
        }
    }

    /// <summary>Stops the source and gives everything back.</summary>
    public void Release()
    {
        _finished = true;
        if (_source != 0)
        {
            uint source = _source;
            _source = 0;
            AL.AlSourceStop(source);
            AL.AlSourcei(source, AL.AlBuffer, 0); // detaches the buffer, or the queue of a stream
            AL.AlDeleteSources(1, &source);
        }

        if (_filter != 0)
        {
            uint filter = _filter;
            _filter = 0;
            AL.AlDeleteFilters(1, &filter);
        }

        _stream?.Dispose();
        _stream = null;
    }
}
