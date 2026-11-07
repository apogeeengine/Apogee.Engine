using System;
using System.IO;
using Apogee.Native.AudioDecode;
using Xunit;
using DecodeApi = Apogee.Native.AudioDecode.AudioDecode;

namespace Apogee.Native.Tests;

/// <summary>Our audio decoder library opens WAV, FLAC, Ogg Vorbis and MP3 from memory and gives float samples.</summary>
public unsafe class AudioDecodeSmokeTests
{
    private static void Require() => NativeRequirements.Require(typeof(DecodeApi).Assembly, "audio_decode");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio", name);

    /// <summary>Decodes a whole file: its info and every sample of its first channel.</summary>
    private static (AaInfo Info, float[] Samples) Decode(string file)
    {
        byte[] bytes = File.ReadAllBytes(Fixture(file));
        fixed (byte* data = bytes)
        {
            AaInfo info;
            AaDecoder* decoder = DecodeApi.Open(data, (nuint)bytes.Length, &info);
            Assert.True(decoder != null, $"{file} did not open");
            try
            {
                float[] all = new float[(int)info.Channels * 0x4000];
                ulong frames;
                fixed (float* buffer = all)
                {
                    frames = DecodeApi.Read(decoder, buffer, 0x4000);
                    Assert.Equal(0ul, DecodeApi.Read(decoder, buffer, 16)); // the end of the file
                }

                return (info, all.AsSpan(0, (int)frames * (int)info.Channels).ToArray());
            }
            finally
            {
                DecodeApi.Close(decoder);
            }
        }
    }

    [Theory]
    [InlineData("tone.wav", 22050)]
    [InlineData("tone.flac", 22050)]
    [InlineData("tone.ogg", 22050)]
    [InlineData("tone.mp3", 44100)]
    public void EveryFormatDecodesToAToneOfTheRightLength(string file, int sampleRate)
    {
        Require();
        (AaInfo info, float[] samples) = Decode(file);

        Assert.Equal(1u, info.Channels);
        Assert.Equal((uint)sampleRate, info.SampleRate);
        // A quarter of a second (the lossy formats pad a little at the ends).
        Assert.InRange(samples.Length, sampleRate / 4 - 50, sampleRate / 4 + 2400);

        // ffmpeg's sine has an amplitude of 1/8 (rms 0.088), and 440 Hz crosses zero about 440 * 2 * 0.25 times.
        double sum = 0;
        for (int i = samples.Length / 4; i < samples.Length * 3 / 4; i++)
        {
            sum += samples[i] * samples[i];
        }

        double rms = Math.Sqrt(sum / (samples.Length / 2));
        Assert.InRange(rms, 0.07, 0.11);
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 != samples[i] < 0)
            {
                crossings++;
            }
        }

        Assert.InRange(crossings, 205, 235);
    }

    [Fact]
    public void RewindStartsOver()
    {
        Require();
        byte[] bytes = File.ReadAllBytes(Fixture("tone.ogg"));
        fixed (byte* data = bytes)
        {
            AaInfo info;
            AaDecoder* decoder = DecodeApi.Open(data, (nuint)bytes.Length, &info);
            Assert.True(decoder != null);
            float[] first = new float[256];
            float[] again = new float[256];
            fixed (float* a = first)
            fixed (float* b = again)
            {
                Assert.Equal(256ul, DecodeApi.Read(decoder, a, 256));
                Assert.Equal(1, DecodeApi.Rewind(decoder));
                Assert.Equal(256ul, DecodeApi.Read(decoder, b, 256));
            }

            Assert.Equal(first, again);
            DecodeApi.Close(decoder);
        }
    }

    [Fact]
    public void GarbageDoesNotOpen()
    {
        Require();
        byte[] garbage = new byte[2000];
        new Random(3).NextBytes(garbage);
        fixed (byte* data = garbage)
        {
            AaInfo info;
            Assert.True(DecodeApi.Open(data, (nuint)garbage.Length, &info) == null);
            Assert.True(DecodeApi.Open(data, 2, &info) == null);
        }
    }
}
