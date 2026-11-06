using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Content.FileSystem;
using Apogee.Core.Logging;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Interop;
using Apogee.Native.OpenAL;
using Apogee.Physics;
using Apogee.Reflection;
using AL = Apogee.Native.OpenAL.OpenAL;

namespace Apogee.Audio;

/// <summary>
/// The game's audio, on OpenAL Soft: sounds (WAV, FLAC, MP3, Ogg Vorbis) played in 3D around the entity that has an <see cref="AudioListener"/>, on buses, with reverb zones and occlusion. OpenAL Soft is a
/// separate shared library that ships beside the program (see Legal/THIRD_PARTY_NOTICES.md): without it <see cref="IsActive"/> is false, every call does nothing and returns something inert, and the game runs in silence.
/// </summary>
/// <remarks>
/// Call <see cref="Update"/> once a frame, after the world has progressed (it reads the entities' world transforms). Files are read through <see cref="Files"/>, so sounds live in <c>res://</c>, mods and packs.
/// One audio system per process: OpenAL's current context is process-wide. The OpenAL listener stays at the origin; every voice is placed relative to the nearest <see cref="AudioListener"/>, in double
/// precision, so sound stays exact far from the world's origin. To use another audio middleware, replace this library (the engine has no backend layer).
/// </remarks>
[ApogeeClass]
public sealed unsafe partial class AudioSystem : EngineObject, IDisposable
{
    private static int s_warned;

    private readonly World _ecs;
    private readonly AudioOptions _options;
    private readonly Dictionary<string, AudioBus> _buses = new(StringComparer.Ordinal);
    private readonly List<Playback> _playbacks = [];
    private readonly Query<AudioListener, WorldTransform> _listenersQuery;
    private readonly Query<ReverbZone, WorldTransform> _zones;
    private readonly Dictionary<byte, Double3> _lastListenerPositions = [];
    private readonly List<ListenerState> _listeners = [];
    private int _reverbKey = -1;
    private bool _disposed;
    private PhysicsWorld? _physics;
    private float _masterVolume = 1;

    /// <summary>Makes the audio of an ECS world. If OpenAL Soft is not installed (or will not start) the system is inactive and says so once in the log.</summary>
    public AudioSystem(World ecs, AudioOptions? options = null, VirtualFileSystem? files = null)
    {
        _ecs = ecs;
        _options = options ?? new AudioOptions();
        Files = files;
        _ecs.Component<AudioListener>();
        _ecs.Component<ReverbZone>();
        _listenersQuery = _ecs.Query<AudioListener, WorldTransform>();
        _zones = _ecs.Query<ReverbZone, WorldTransform>();

        if (!IsInstalled())
        {
            Warn("OpenAL Soft is not installed (the 'openal' library did not load): audio stays silent.");
            return;
        }

        OpenDevice();
    }

    /// <summary>Makes an audio system for an ECS world from a script, mixing into nowhere (no sound device is opened): for tests. A game's own is the global <c>audio</c>.</summary>
    [Method]
    public static AudioSystem Create(World ecs) => new(ecs, new AudioOptions { Output = AudioOutput.None });

    /// <summary>Whether OpenAL Soft and the decoders can be loaded: false on a machine without the libraries.</summary>
    [Method]
    public static bool IsInstalled() =>
        NativeLibraries.TryLoad(typeof(AL).Assembly, "openal", out _) && NativeLibraries.TryLoad(typeof(Apogee.Native.AudioDecode.AudioDecode).Assembly, "audio_decode", out _);

    /// <summary>True when OpenAL started: sounds will play. False means every call is a no-op.</summary>
    [Property]
    public bool IsActive => _context != null && !_disposed;

    /// <summary>The file system the sounds are read from (<c>res://…</c>).</summary>
    public VirtualFileSystem? Files { get; set; }

    /// <summary>The physics world that casts the rays for <see cref="AudioVoice.Occlusion"/>.</summary>
    [Property]
    public PhysicsWorld? Physics
    {
        get => _physics;
        set => _physics = value;
    }

    /// <summary>The master volume, 0 to 1 and above.</summary>
    [Property]
    public float Volume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Max(0, value);
            if (IsActive)
            {
                AL.AlListenerf(AL.AlGain, _masterVolume);
            }
        }
    }

    internal World Ecs => _ecs;

    internal AudioOptions Options => _options;

    internal PhysicsWorld? PhysicsWorld => _physics;

    internal static void Warn(string message)
    {
        if (System.Threading.Interlocked.Exchange(ref s_warned, 1) == 0)
        {
            Log.Warn("Audio", message);
        }
    }

    /// <summary>Logs an OpenAL error left by the calls since the last check; true when there was none.</summary>
    internal static bool Ok(string what)
    {
        int error = AL.AlGetError();
        if (error == AL.AlNoError)
        {
            return true;
        }

        Log.Warn("Audio", $"OpenAL could not {what}: error 0x{error:X}");
        return false;
    }

    // ---- The frame --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Once a frame, after the world has progressed: finds the listeners, puts the voices where their entities are (relative to the nearest listener, in double precision, with the velocities the Doppler
    /// effect needs), raycasts the occlusion, picks the reverb zone, and mixes the time that has passed when the output is not a device. <paramref name="deltaSeconds"/> is the game's frame time.
    /// </summary>
    [Method]
    public void Update(float deltaSeconds)
    {
        if (!IsActive)
        {
            return;
        }

        UpdateListeners(deltaSeconds);
        UpdateReverb();
        if (_offline)
        {
            // The sources are placed once, then the time is mixed in blocks, with the streams topped up between them.
            RefreshPlaybacks(deltaSeconds);
            Render(deltaSeconds);
        }
        else
        {
            AL.AlcSuspendContext(_context);
            RefreshPlaybacks(deltaSeconds);
            AL.AlcProcessContext(_context);
        }

        Ok("update");
    }

    private void RefreshPlaybacks(float dt)
    {
        for (int i = _playbacks.Count - 1; i >= 0; i--)
        {
            if (!_playbacks[i].Refresh(dt))
            {
                _playbacks.RemoveAt(i);
            }
        }
    }

    internal void PumpStreams()
    {
        foreach (Playback playback in _playbacks)
        {
            playback.Pump();
        }
    }

    /// <summary>The reverb the listener hears now: the preset of the zone it is in, or <see cref="ReverbPreset.Off"/>.</summary>
    [Property]
    public ReverbPreset CurrentReverb { get; private set; }

    internal readonly record struct ListenerState(byte Index, Double3 Position, Quaternion Rotation, Vector3 Velocity);

    internal bool HasListener => _listeners.Count > 0;

    /// <summary>The listener a position is heard by: the nearest one (the origin, looking along -Z, when the world has none).</summary>
    internal ListenerState NearestListener(Double3 position)
    {
        if (_listeners.Count == 0)
        {
            return new ListenerState(0, Double3.Zero, Quaternion.Identity, Vector3.Zero);
        }

        ListenerState best = _listeners[0];
        double bestDistance = double.MaxValue;
        foreach (ListenerState listener in _listeners)
        {
            Double3 d = position - listener.Position;
            double distance = (d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z);
            if (distance < bestDistance)
            {
                (best, bestDistance) = (listener, distance);
            }
        }

        return best;
    }

    private void UpdateListeners(float dt)
    {
        var found = new List<(byte Index, WorldTransform Transform)>();
        _listenersQuery.Each((Entity _, ref AudioListener listener, ref WorldTransform transform) => found.Add((listener.Index, transform)));
        found.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        _listeners.Clear();
        for (int i = 0; i < Math.Min(found.Count, 8); i++)
        {
            (byte index, WorldTransform t) = found[i];
            Vector3 velocity = Vector3.Zero;
            if (_lastListenerPositions.TryGetValue(index, out Double3 last) && dt > 1e-6f)
            {
                velocity = (Vector3)(t.Position - last) / dt;
            }

            _lastListenerPositions[index] = t.Position;
            _listeners.Add(new ListenerState(index, t.Position, t.Rotation, velocity));
        }
    }

    private void UpdateReverb()
    {
        if (_listeners.Count == 0)
        {
            return;
        }

        // The zone the first listener is in: highest priority, then smallest.
        int bestPriority = int.MinValue;
        float bestVolume = float.MaxValue;
        ReverbPreset bestPreset = ReverbPreset.Off;
        float bestLevel = 0;
        Double3 at = _listeners[0].Position;
        _zones.Each((Entity _, ref ReverbZone zone, ref WorldTransform transform) =>
        {
            Vector3 local = Vector3.Transform((Vector3)(at - transform.Position), Quaternion.Inverse(transform.Rotation)) / transform.Scale;
            Vector3 half = zone.HalfExtents;
            if (MathF.Abs(local.X) > half.X || MathF.Abs(local.Y) > half.Y || MathF.Abs(local.Z) > half.Z)
            {
                return;
            }

            float volume = half.X * half.Y * half.Z * transform.Scale.X * transform.Scale.Y * transform.Scale.Z;
            if (zone.Priority > bestPriority || (zone.Priority == bestPriority && volume < bestVolume))
            {
                (bestPriority, bestVolume, bestPreset, bestLevel) = (zone.Priority, volume, (ReverbPreset)zone.Preset, zone.Level);
            }
        });

        CurrentReverb = bestPreset;
        int key = ((int)bestPreset << 16) ^ BitConverter.SingleToInt32Bits(bestLevel);
        if (key != _reverbKey)
        {
            _reverbKey = key;
            SetReverb(ReverbPresets.Of(bestPreset, bestLevel));
        }
    }

    internal void Track(Playback playback) => _playbacks.Add(playback);

    // ---- Buses ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A bus by name, made when first asked for: the groups of sounds a game mixes (<c>music</c>, <c>vehicles</c>, <c>ui</c>), each with a volume, a mute and a pause of its own. The empty name and
    /// <c>master</c> are the whole mix (its volume is <see cref="Volume"/>). Buses are cached: asking twice gives the same object.
    /// </summary>
    [Method]
    public AudioBus GetBus(string name)
    {
        string key = name.Length == 0 ? "master" : name;
        if (_buses.TryGetValue(key, out AudioBus? bus) && !bus.IsDestroyed)
        {
            return bus;
        }

        bus = new AudioBus(this, key);
        bus.Adopt();
        _buses[key] = bus;
        return bus;
    }

    /// <summary>Called when a bus's volume, mute or pause changed: its voices follow.</summary>
    internal void BusChanged(AudioBus bus)
    {
        if (!IsActive)
        {
            return;
        }

        foreach (Playback playback in _playbacks)
        {
            if (bus.IsMaster || playback.Bus == bus)
            {
                playback.Apply();
            }
        }
    }

    internal void StopBus(AudioBus bus)
    {
        foreach (Playback playback in _playbacks.ToArray())
        {
            if (bus.IsMaster || playback.Bus == bus)
            {
                playback.Stop();
            }
        }
    }

    /// <summary>The sound is going away: whatever is playing it is cut.</summary>
    internal void StopSound(AudioSound sound)
    {
        foreach (Playback playback in _playbacks.ToArray())
        {
            if (playback.Sound == sound)
            {
                playback.Stop();
            }
        }
    }

    // ---- Sounds -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Loads a sound file (WAV, FLAC, MP3 or Ogg Vorbis) from the file system. <paramref name="stream"/> decodes it as it plays instead of whole (music, long ambience);
    /// <paramref name="is3D"/> makes it positional; <paramref name="loop"/> loops it by default.
    /// </summary>
    [Method]
    public AudioSound LoadSound(string path, bool stream = false, bool is3D = true, bool loop = false)
    {
        if (!IsActive)
        {
            return new AudioSound(this, path, is3D, stream, 0, null, 0, 0, 0);
        }

        byte[] bytes = AudioFiles.Read(Files, path);
        var pinned = new PinnedBytes(bytes);
        SoundDecoder? decoder = SoundDecoder.Open(pinned);
        if (decoder is null)
        {
            pinned.Dispose();
            throw new ValueException($"could not load the sound {path}: it is not a WAV, FLAC, MP3 or Ogg Vorbis file we can read");
        }

        using (decoder)
        {
            if (stream)
            {
                return new AudioSound(this, path, is3D, true, 0, pinned, decoder.Channels, decoder.SampleRate, decoder.Frames) { LoopsByDefault = loop };
            }

            // Decoded whole into one buffer.
            using (pinned)
            {
                float[] samples = new float[Math.Max(decoder.Frames, 4096) * decoder.Channels];
                int frames = 0;
                while (true)
                {
                    if ((frames + 4096) * decoder.Channels > samples.Length)
                    {
                        Array.Resize(ref samples, samples.Length * 2);
                    }

                    int got = decoder.Read(samples.AsSpan(frames * decoder.Channels), 4096);
                    if (got <= 0)
                    {
                        break;
                    }

                    frames += got;
                }

                uint buffer = MakeBuffer(samples.AsSpan(0, frames * decoder.Channels), decoder.Channels, decoder.SampleRate);
                if (buffer == 0)
                {
                    throw new ValueException($"could not load the sound {path}: OpenAL refused its samples");
                }

                return new AudioSound(this, path, is3D, false, buffer, null, decoder.Channels, decoder.SampleRate, frames) { LoopsByDefault = loop };
            }
        }
    }

    internal static int FormatOf(int channels) => channels == 1 ? AL.AlFormatMonoFloat32 : AL.AlFormatStereoFloat32;

    internal static uint MakeBuffer(ReadOnlySpan<float> samples, int channels, int rate)
    {
        uint buffer;
        AL.AlGenBuffers(1, &buffer);
        fixed (float* data = samples)
        {
            AL.AlBufferData(buffer, FormatOf(channels), data, samples.Length * sizeof(float), rate);
        }

        if (!Ok("fill a buffer"))
        {
            AL.AlDeleteBuffers(1, &buffer);
            return 0;
        }

        return buffer;
    }

    // ---- Lifetime ---------------------------------------------------------------------------------------------------------------

    public void Dispose() => Destroy();

    protected override void OnDestroyed()
    {
        if (_disposed)
        {
            return;
        }

        if (_context == null)
        {
            _disposed = true;
            return;
        }

        foreach (Playback playback in _playbacks.ToArray())
        {
            playback.Release();
        }

        _playbacks.Clear();
        _disposed = true;
        CloseDevice();
    }
}
