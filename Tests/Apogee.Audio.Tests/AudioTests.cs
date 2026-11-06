using System;
using System.IO;
using Apogee.Core.Numerics;
using Apogee.Reflection;
using Entity = Apogee.Reflection.Entity;
using Xunit;

namespace Apogee.Audio.Tests;

// OpenAL's current context is process-wide, so the classes that start an audio system run one at a time.
[Collection("Audio")]
public class AudioTests : AudioFixture
{
    [Fact]
    public void ASoundFromTheFileSystemIsMixedIntoTheOutput()
    {
        string path = Write("tone.wav", WavFile.Tone(440, 1));
        Listener();
        AudioSound sound = Audio.LoadSound(path, is3D: false);
        Assert.True(Audio.IsActive);
        Assert.Equal(1f, sound.Length, 0.01f);

        AudioVoice voice = sound.Play();
        Run(0.5);
        bool playing = voice.IsPlaying;
        Run(1.5);
        WavFile wav = Finish();

        Assert.True(playing);
        Assert.Equal(2, wav.Channels);
        Assert.Equal(48000, wav.SampleRate);
        double early = wav.Rms(0, 0.1, 0.5);
        double late = wav.Rms(0, 1.4, 1.9);
        Assert.True(early > 0.2, $"rms {early}");  // a half-scale sine has an rms of 0.35 (a 2D sound is centered, a little less per channel)
        Assert.True(late < 0.001, $"rms {late}");   // it ended
        Assert.False(voice.IsPlaying);
    }

    [Fact]
    public void ASoundThatIsMissingOrNotASoundIsAnErrorWithTheReason()
    {
        Listener();
        Assert.Throws<ValueException>(() => Audio.LoadSound("res://nope.wav"));
        Write("garbage.wav", new byte[200]);
        Assert.Throws<ValueException>(() => Audio.LoadSound("res://garbage.wav"));
    }

    [Fact]
    public void ASourceToTheLeftIsLouderInTheLeftSpeaker()
    {
        string path = Write("tone.wav", WavFile.Tone(440, 3));
        Listener(); // looks along -Z, so +X is its right
        Entity left = Emitter(new Double3(-4, 0, -1));
        Audio.LoadSound(path).Play(entity: left);
        Run(1);
        WavFile first = Finish();
        double leftEar = first.Rms(0, 0.2, 1);
        double rightEar = first.Rms(1, 0.2, 1);
        Assert.True(leftEar > rightEar * 2, $"left {leftEar}, right {rightEar}");
    }

    [Fact]
    public void ASourceToTheRightIsLouderInTheRightSpeakerAndMovingItMovesTheSound()
    {
        string path = Write("tone.wav", WavFile.Tone(440, 4));
        Listener();
        Entity source = Emitter(new Double3(4, 0, -1));
        Audio.LoadSound(path, loop: true).Play(entity: source);
        Run(1);
        // Swing it to the other side while it plays.
        Ecs.Set(source, new Apogee.Ecs.Transform(new Double3(-4, 0, -1), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One));
        Run(1);
        WavFile wav = Finish();

        Assert.True(wav.Rms(1, 0.3, 0.9) > wav.Rms(0, 0.3, 0.9) * 2, "right at first");
        Assert.True(wav.Rms(0, 1.3, 1.9) > wav.Rms(1, 1.3, 1.9) * 2, "left after");
    }

    [Fact]
    public void ADistantSourceIsQuieter()
    {
        string path = Write("tone.wav", WavFile.Tone(440, 3));
        Listener();
        Audio.LoadSound(path).Play(entity: Emitter(new Double3(0, 0, -2)));
        Run(0.8);
        WavFile near = Finish();
        double nearRms = near.Rms(0, 0.2, 0.8) + near.Rms(1, 0.2, 0.8);

        // A second run, far away.
        using var far = new FarRun(this);
        double farRms = far.Measure(new Double3(0, 0, -40));
        Assert.True(farRms < nearRms * 0.3, $"near {nearRms}, far {farRms}");
    }

    // The same mix again with the source further away, in a world of its own.
    private sealed class FarRun : AudioFixture
    {
        public FarRun(AudioFixture _)
        {
        }

        public double Measure(Double3 at)
        {
            string path = Write("tone.wav", WavFile.Tone(440, 3));
            Listener();
            Audio.LoadSound(path).Play(entity: Emitter(at));
            Run(0.8);
            WavFile wav = Finish();
            return wav.Rms(0, 0.2, 0.8) + wav.Rms(1, 0.2, 0.8);
        }
    }

    [Fact]
    public void ABusScalesWhatPlaysOnIt()
    {
        string path = Write("tone.wav", WavFile.Tone(440, 3));
        Listener();
        AudioSound sound = Audio.LoadSound(path, is3D: false);
        AudioBus loud = Audio.GetBus("loud");
        AudioBus quiet = Audio.GetBus("quiet");
        quiet.Volume = 0.25f;
        Assert.Same(loud, Audio.GetBus("loud"));

        sound.Play("quiet");
        Run(0.6);
        WavFile onlyQuiet = Finish();
        double quietRms = onlyQuiet.Rms(0, 0.2, 0.6);
        Assert.True(quietRms > 0.02);

        using var other = new BusRun();
        double loudRms = other.Measure();
        Assert.Equal(4.0, loudRms / quietRms, 0.4);
    }

    private sealed class BusRun : AudioFixture
    {
        public double Measure()
        {
            string path = Write("tone.wav", WavFile.Tone(440, 3));
            Listener();
            Audio.LoadSound(path, is3D: false).Play("loud");
            Run(0.6);
            return Finish().Rms(0, 0.2, 0.6);
        }
    }

    [Fact]
    public void MutingAndPausingABusSilencesIt()
    {
        string path = Write("tone.wav", WavFile.Tone(440, 3));
        Listener();
        AudioBus bus = Audio.GetBus("music");
        Audio.LoadSound(path, is3D: false, loop: true).Play("music");
        Run(0.4);
        bus.Muted = true;
        Run(0.4);
        bus.Muted = false;
        bus.SetPaused(true);
        Run(0.4);
        bus.SetPaused(false);
        Run(0.4);
        WavFile wav = Finish();

        Assert.True(wav.Rms(0, 0.1, 0.4) > 0.05, "playing");
        Assert.True(wav.Rms(0, 0.5, 0.8) < 0.001, "muted");
        Assert.True(wav.Rms(0, 0.9, 1.2) < 0.001, "paused");
        Assert.True(wav.Rms(0, 1.3, 1.6) > 0.05, "playing again");
    }

    [Fact]
    public void AGatheredVoiceCanBeStoppedAndPitched()
    {
        string path = Write("tone.wav", WavFile.Tone(440, 5));
        Listener();
        AudioVoice voice = Audio.LoadSound(path, is3D: false, loop: true).Play(volume: 0.5f, pitch: 2f);
        Run(0.5);
        Assert.Equal(2f, voice.Pitch);
        voice.Volume = 0.25f;
        voice.Stop();
        Run(0.5);
        Assert.False(voice.IsPlaying);
        WavFile wav = Finish();
        Assert.True(wav.Rms(0, 0.1, 0.45) > 0.03);
        Assert.True(wav.Rms(0, 0.6, 0.95) < 0.001);
    }

    [Fact]
    public void AReverbZoneGivesTheSoundATailThatOutsideItDoesNotHave()
    {
        double inside = TailAfterTheSound(withZone: true);
        double outside = TailAfterTheSound(withZone: false);

        Assert.True(outside < 0.0005, $"no zone: {outside}");
        Assert.True(inside > 0.003, $"zone: {inside}");
    }

    private static double TailAfterTheSound(bool withZone)
    {
        using var run = new ReverbRun();
        return run.Measure(withZone);
    }

    private sealed class ReverbRun : AudioFixture
    {
        public double Measure(bool withZone)
        {
            string path = Write("blip.wav", WavFile.Tone(440, 0.3));
            Listener();
            if (withZone)
            {
                Apogee.Reflection.Entity zone = Ecs.CreateEntity();
                Ecs.Set(zone, new Apogee.Ecs.Transform(Double3.Zero, System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One));
                Ecs.Set(zone, new ReverbZone { HalfExtents = new System.Numerics.Vector3(10, 10, 10), Preset = (byte)ReverbPreset.Cave, Priority = 1 });
            }

            Audio.LoadSound(path, is3D: false).Play();
            Run(2);
            return Finish().Rms(0, 0.8, 1.6);
        }
    }

    [Fact]
    public void ZonesAreChosenByPriorityThenSizeAndOnlyWhereTheListenerIs()
    {
        Listener(new Double3(100, 0, 0));
        AudioSystem audio = Audio;
        Apogee.Reflection.Entity far = Ecs.CreateEntity();
        Ecs.Set(far, new Apogee.Ecs.Transform(Double3.Zero, System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One));
        Ecs.Set(far, new ReverbZone { HalfExtents = new System.Numerics.Vector3(5), Preset = (byte)ReverbPreset.Garage, Priority = 9 });
        Run(0.1);
        Assert.Equal(ReverbPreset.Off, audio.CurrentReverb); // the listener is not in it

        Ecs.Set(Ecs.CreateEntity(), new Apogee.Ecs.Transform(new Double3(100, 0, 0), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One));
        Apogee.Reflection.Entity big = Ecs.CreateEntity();
        Ecs.Set(big, new Apogee.Ecs.Transform(new Double3(100, 0, 0), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One));
        Ecs.Set(big, new ReverbZone { HalfExtents = new System.Numerics.Vector3(50), Preset = (byte)ReverbPreset.Forest, Priority = 1 });
        Run(0.1);
        Assert.Equal(ReverbPreset.Forest, audio.CurrentReverb);

        Apogee.Reflection.Entity small = Ecs.CreateEntity();
        Ecs.Set(small, new Apogee.Ecs.Transform(new Double3(100, 0, 0), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One));
        Ecs.Set(small, new ReverbZone { HalfExtents = new System.Numerics.Vector3(5), Preset = (byte)ReverbPreset.Room, Priority = 1 });
        Run(0.1);
        Assert.Equal(ReverbPreset.Room, audio.CurrentReverb); // same priority: the smaller one

        Apogee.Reflection.Entity priority = Ecs.CreateEntity();
        Ecs.Set(priority, new Apogee.Ecs.Transform(new Double3(100, 0, 0), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One));
        Ecs.Set(priority, new ReverbZone { HalfExtents = new System.Numerics.Vector3(20), Preset = (byte)ReverbPreset.Hangar, Priority = 5 });
        Run(0.1);
        Assert.Equal(ReverbPreset.Hangar, audio.CurrentReverb);
    }
}

// OpenAL's current context is process-wide, so the classes that start an audio system run one at a time.
[Collection("Audio")]
public class OcclusionTests : AudioFixture
{
    private static double Measure(bool wall)
    {
        using var run = new OcclusionRun();
        return run.Measure(wall);
    }

    private sealed class OcclusionRun : AudioFixture
    {
        public double Measure(bool wall)
        {
            using var physics = new Apogee.Physics.PhysicsWorld(Ecs, new Apogee.Physics.PhysicsOptions { ThreadCount = 1, TempBufferBytes = 2 * 1024 * 1024, MaxBodies = 64 });
            Audio.Physics = physics;
            if (wall)
            {
                Apogee.Reflection.Entity block = Ecs.CreateEntity();
                Ecs.Set(block, new Apogee.Ecs.Transform(new Double3(0, 0, -5), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One));
                physics.AddBody(block, new Apogee.Physics.BodyDesc { Shape = Apogee.Physics.PhysicsShape.Box(new System.Numerics.Vector3(10, 10, 0.5f)), Motion = Apogee.Physics.MotionKind.Static });
            }

            string path = Write("tone.wav", WavFile.Tone(440, 4));
            Listener();
            AudioVoice voice = Audio.LoadSound(path).Play(entity: Emitter(new Double3(0, 0, -10)), loop: true);
            voice.Occlusion = true;
            for (int i = 0; i < 60; i++)
            {
                physics.Update(1f / 60);
                Ecs.Progress(1f / 60);
                Audio.Update(1024f / 48000);
            }

            double muffling = voice.Muffling;
            WavFile wav = Finish();
            return wav.Rms(0, 0.8, 1.2) + wav.Rms(1, 0.8, 1.2) + (muffling * 1000);
        }
    }

    [Fact]
    public void AWallBetweenTheListenerAndTheSourceMuffleIt()
    {
        double clear = Measure(wall: false);
        double blocked = Measure(wall: true);

        // The muffling (x1000, to ride along in the number) is 0 in the clear and near 1 behind the wall; the sound itself is quieter.
        Assert.True(clear < 500, $"clear: {clear}");
        Assert.True(blocked > 900, $"blocked: {blocked}");
        double clearLevel = clear;
        double blockedLevel = blocked - 1000;
        Assert.True(blockedLevel < clearLevel * 0.8, $"clear {clearLevel}, blocked {blockedLevel}");
    }
}

// OpenAL's current context is process-wide, so the classes that start an audio system run one at a time.
[Collection("Audio")]
public class AudioFileTests : AudioFixture
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio", name));

    [Theory]
    [InlineData("tone.wav")]
    [InlineData("tone.flac")]
    [InlineData("tone.ogg")]
    [InlineData("tone.mp3")]
    public void EveryFormatPlays(string file)
    {
        string path = Write(file, Fixture(file));
        Listener();
        AudioSound sound = Audio.LoadSound(path, is3D: false);
        Assert.Equal(0.25f, sound.Length, 0.02f);
        sound.Play();
        Run(0.2);
        WavFile wav = Finish();
        Assert.True(wav.Rms(0, 0.03, 0.18) > 0.03, $"rms {wav.Rms(0, 0.03, 0.18)}");
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("tone.wav")]
    public void OnlyVirtualPathsAreRead(string path)
    {
        Listener();
        ValueException error = Assert.Throws<ValueException>(() => Audio.LoadSound(path));
        Assert.Contains("file system", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamPlaysToItsEndAndAStreamedLoopGoesOn()
    {
        string path = Write("long.ogg", Fixture("tone.ogg"));
        string wavPath = Write("long.wav", WavFile.Tone(440, 2));
        Listener();
        AudioSound once = Audio.LoadSound(wavPath, stream: true, is3D: false);
        AudioSound loop = Audio.LoadSound(path, stream: true, is3D: false, loop: true);
        Assert.Equal(2f, once.Length, 0.02f);
        AudioVoice ends = once.Play();
        AudioVoice goesOn = loop.Play(volume: 0.5f);
        Run(1);
        Assert.True(ends.IsPlaying);
        Assert.InRange(ends.Position, 0.8f, 1.2f);
        Run(2);
        bool endedAfterThree = !ends.IsPlaying;
        bool loopStillOn = goesOn.IsPlaying;
        goesOn.Stop();
        WavFile wav = Finish();

        Assert.True(endedAfterThree);
        Assert.True(loopStillOn); // the quarter-second tone, looped for three seconds
        Assert.True(wav.Rms(0, 0.2, 1.8) > 0.2, "the stream is heard");
        Assert.True(wav.Rms(0, 2.2, 2.9) > 0.01, "the loop is heard after the other ended");
    }

    [Fact]
    public void AVoiceHasItsOwnDistancesAndTheMasterBusMutesTheMix()
    {
        string path = Write("tone.wav", WavFile.Tone(440, 4));
        Listener();
        // Both 20 m away on either side; the left one is not made quieter until 100 m.
        AudioSound sound = Audio.LoadSound(path);
        AudioVoice near = sound.Play(entity: Emitter(new Double3(-20, 0, 0)), loop: true);
        near.MinDistance = 100;
        near.MaxDistance = 200;
        sound.Play(entity: Emitter(new Double3(20, 0, 0)), loop: true);
        Run(0.5);
        Audio.GetBus("master").Muted = true;
        Run(0.5);
        WavFile wav = Finish();

        double left = wav.Rms(0, 0.2, 0.5);
        double right = wav.Rms(1, 0.2, 0.5);
        Assert.True(left > right * 6, $"left {left}, right {right}");
        Assert.True(wav.Rms(0, 0.6, 1.0) < 0.001, "muted");
    }

    [Fact]
    public void ASoundThatIsDestroyedWhilePlayingIsCut()
    {
        string path = Write("tone.wav", WavFile.Tone(440, 4));
        Listener();
        AudioSound sound = Audio.LoadSound(path, is3D: false);
        AudioVoice voice = sound.Play(loop: true);
        Run(0.3);
        sound.Destroy();
        Run(0.3);
        Assert.False(voice.IsPlaying);
        WavFile wav = Finish();
        Assert.True(wav.Rms(0, 0.1, 0.3) > 0.05);
        Assert.True(wav.Rms(0, 0.4, 0.6) < 0.001);
    }
}
