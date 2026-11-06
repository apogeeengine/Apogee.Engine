using System;
using System.IO;

namespace Apogee.Audio.Tests;

/// <summary>A WAV file read whole: PCM 16-bit or 32-bit float, any channel count. What the audio tests record and measure.</summary>
public sealed class WavFile
{
    private WavFile(int channels, int sampleRate, float[][] samples)
    {
        Channels = channels;
        SampleRate = sampleRate;
        Samples = samples;
    }

    public int Channels { get; }

    public int SampleRate { get; }

    /// <summary>[channel][frame], -1 to 1.</summary>
    public float[][] Samples { get; }

    public int Frames => Samples[0].Length;

    public static WavFile Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF")
        {
            throw new InvalidDataException("not a WAV file");
        }

        int channels = 0, rate = 0, bits = 0, format = 0, dataStart = 0, dataLength = 0;
        for (int at = 12; at + 8 <= bytes.Length;)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            int length = BitConverter.ToInt32(bytes, at + 4);
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(bytes, at + 8);
                channels = BitConverter.ToUInt16(bytes, at + 10);
                rate = BitConverter.ToInt32(bytes, at + 12);
                bits = BitConverter.ToUInt16(bytes, at + 22);
            }
            else if (id == "data")
            {
                dataStart = at + 8;
                dataLength = length <= 0 || dataStart + length > bytes.Length ? bytes.Length - dataStart : length;
                break;
            }

            at += 8 + length + (length & 1);
        }

        int bytesPerSample = bits / 8;
        int frames = dataLength / (bytesPerSample * channels);
        var samples = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            samples[c] = new float[frames];
        }

        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                int at = dataStart + (((i * channels) + c) * bytesPerSample);
                samples[c][i] = format == 3 ? BitConverter.ToSingle(bytes, at) : bits == 16 ? BitConverter.ToInt16(bytes, at) / 32768f : throw new InvalidDataException($"{bits}-bit format {format}");
            }
        }

        return new WavFile(channels, rate, samples);
    }

    /// <summary>The root mean square of a channel over seconds [from, to).</summary>
    public double Rms(int channel, double from, double to)
    {
        int start = Math.Clamp((int)(from * SampleRate), 0, Frames);
        int end = Math.Clamp((int)(to * SampleRate), start, Frames);
        if (end == start)
        {
            return 0;
        }

        double sum = 0;
        for (int i = start; i < end; i++)
        {
            sum += Samples[channel][i] * Samples[channel][i];
        }

        return Math.Sqrt(sum / (end - start));
    }

    /// <summary>The seconds of audio in the file.</summary>
    public double Seconds => (double)Frames / SampleRate;

    /// <summary>A mono 16-bit sine at <paramref name="hertz"/>, <paramref name="seconds"/> long, as a WAV file's bytes.</summary>
    public static byte[] Tone(double hertz, double seconds, double amplitude = 0.5, int rate = 48000)
    {
        int frames = (int)(seconds * rate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + (frames * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 2);
        for (int i = 0; i < frames; i++)
        {
            writer.Write((short)(Math.Sin(2 * Math.PI * hertz * i / rate) * amplitude * 32767));
        }

        return stream.ToArray();
    }
}
