using System;
using System.IO;
using System.Numerics;
using Apogee.Content.FileSystem;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;

namespace Apogee.Audio.Tests;

/// <summary>
/// An ECS world and an audio system that mixes the game's time into a WAV file, which <see cref="Finish"/> closes and reads. The tests skip without OpenAL Soft installed.
/// </summary>
public abstract class AudioFixture : IDisposable
{
    private readonly string _folder;
    private AudioSystem? _audio;
    private bool _finished;

    protected AudioFixture()
    {
        NativeRequirements.Require(typeof(Apogee.Native.OpenAL.OpenAL).Assembly, "openal");
        NativeRequirements.Require(typeof(Apogee.Native.AudioDecode.AudioDecode).Assembly, "audio_decode");
        NativeRequirements.Require(typeof(Apogee.Native.Flecs.Ecs).Assembly, "flecs");
        _folder = Path.Combine(Path.GetTempPath(), "apogee-audio-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
        Ecs = new World();
        Files = new VirtualFileSystem();
        Files.Mount("res://", new DirectorySource(_folder, writable: true));
    }

    protected World Ecs { get; }

    protected VirtualFileSystem Files { get; }

    protected string WavPath => Path.Combine(_folder, "out.wav");

    protected AudioSystem Audio => _audio ??= new AudioSystem(Ecs, new AudioOptions { Output = AudioOutput.WavFile, WavPath = WavPath }, Files);

    protected string Write(string name, byte[] bytes)
    {
        Files.WriteAllBytes("res://" + name, bytes);
        return "res://" + name;
    }

    protected Entity Listener(Double3? at = null)
    {
        Entity entity = Ecs.CreateEntity();
        Ecs.Set(entity, new Transform(at ?? Double3.Zero, Quaternion.Identity, Vector3.One));
        Ecs.Set(entity, new AudioListener());
        return entity;
    }

    protected Entity Emitter(Double3 at)
    {
        Entity entity = Ecs.CreateEntity();
        Ecs.Set(entity, new Transform(at, Quaternion.Identity, Vector3.One));
        return entity;
    }

    /// <summary>Runs frames of the game: the world, then the audio, for this many seconds. The audio mixes the time it is given, so the time in the recorded file is the time of the game.</summary>
    protected void Run(double seconds, float frame = 1024f / 48000)
    {
        for (double t = 0; t < seconds - 1e-9; t += frame)
        {
            Ecs.Progress(frame);
            Audio.Update(frame);
        }
    }

    /// <summary>Closes the audio system (which finishes the WAV file) and reads what it wrote.</summary>
    protected WavFile Finish()
    {
        if (!_finished)
        {
            _finished = true;
            Audio.Dispose();
        }

        return WavFile.Read(WavPath);
    }

    public void Dispose()
    {
        if (!_finished && _audio is not null)
        {
            _audio.Dispose();
        }

        Ecs.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
