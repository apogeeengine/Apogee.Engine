using System;
using System.IO;
using System.Runtime.InteropServices;
using Apogee.Content.FileSystem;
using Apogee.Native.AudioDecode;
using Apogee.Reflection;
using DecodeApi = Apogee.Native.AudioDecode.AudioDecode;

namespace Apogee.Audio;

/// <summary>A sound file's bytes in memory that does not move, for the decoders to read while they are open.</summary>
internal sealed unsafe class PinnedBytes : IDisposable
{
    private byte* _data;

    public PinnedBytes(byte[] bytes)
    {
        Length = bytes.Length;
        _data = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
        bytes.AsSpan().CopyTo(new Span<byte>(_data, bytes.Length));
    }

    public byte* Data => _data;

    public int Length { get; }

    public void Dispose()
    {
        if (_data != null)
        {
            NativeMemory.Free(_data);
            _data = null;
        }
    }
}

/// <summary>An open decoder over a <see cref="PinnedBytes"/> file: float samples, interleaved, at the file's channel count (1 or 2 after <see cref="Channels"/> folds the rest) and rate.</summary>
internal sealed unsafe class SoundDecoder : IDisposable
{
    private AaDecoder* _decoder;
    private readonly int _fileChannels;
    private float[] _scratch = [];

    private SoundDecoder(AaDecoder* decoder, AaInfo info)
    {
        _decoder = decoder;
        _fileChannels = (int)info.Channels;
        SampleRate = (int)info.SampleRate;
        Frames = (long)info.Frames;
    }

    /// <summary>1 or 2: files with more channels are folded down to stereo.</summary>
    public int Channels => Math.Min(_fileChannels, 2);

    public int SampleRate { get; }

    /// <summary>The length in frames, or 0 when the file does not say.</summary>
    public long Frames { get; }

    /// <summary>Opens a decoder, or null if the format is unknown or the file is damaged.</summary>
    public static SoundDecoder? Open(PinnedBytes file)
    {
        AaInfo info;
        AaDecoder* decoder = DecodeApi.Open(file.Data, (nuint)file.Length, &info);
        return decoder == null ? null : new SoundDecoder(decoder, info);
    }

    /// <summary>Decodes up to <paramref name="frames"/> frames into <paramref name="output"/> (frames x <see cref="Channels"/> floats); returns how many, 0 at the end.</summary>
    public int Read(Span<float> output, int frames)
    {
        if (_decoder == null)
        {
            return 0;
        }

        if (_fileChannels <= 2)
        {
            fixed (float* target = output)
            {
                return (int)DecodeApi.Read(_decoder, target, (ulong)frames);
            }
        }

        // More than two channels: read them all, then fold them into left (even channels) and right (odd ones).
        if (_scratch.Length < frames * _fileChannels)
        {
            _scratch = new float[frames * _fileChannels];
        }

        int got;
        fixed (float* target = _scratch)
        {
            got = (int)DecodeApi.Read(_decoder, target, (ulong)frames);
        }

        int perSide = (_fileChannels + 1) / 2;
        for (int f = 0; f < got; f++)
        {
            float left = 0;
            float right = 0;
            for (int c = 0; c < _fileChannels; c++)
            {
                if ((c & 1) == 0)
                {
                    left += _scratch[(f * _fileChannels) + c];
                }
                else
                {
                    right += _scratch[(f * _fileChannels) + c];
                }
            }

            output[f * 2] = left / perSide;
            output[(f * 2) + 1] = right / Math.Max(1, _fileChannels / 2);
        }

        return got;
    }

    public bool Rewind() => _decoder != null && DecodeApi.Rewind(_decoder) != 0;

    public void Dispose()
    {
        if (_decoder != null)
        {
            DecodeApi.Close(_decoder);
            _decoder = null;
        }
    }
}

internal static class AudioFiles
{
    /// <summary>Reads a sound file through the game's file system. Only virtual paths (<c>res://</c>, <c>mod://</c>, <c>user://</c>…) are read: a script cannot ask for a file by its place on the disk.</summary>
    public static byte[] Read(VirtualFileSystem? files, string path)
    {
        if (files is null)
        {
            throw new ValueException($"could not load the sound {path}: the audio system has no file system");
        }

        if (!path.Contains("://", StringComparison.Ordinal))
        {
            throw new ValueException($"could not load the sound {path}: sounds are read through the file system (res://…, mod://…, user://…), not from a place on the disk");
        }

        try
        {
            using Stream stream = files.OpenRead(path);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or VfsException)
        {
            throw new ValueException($"could not load the sound {path}: {e.Message}");
        }
    }
}
