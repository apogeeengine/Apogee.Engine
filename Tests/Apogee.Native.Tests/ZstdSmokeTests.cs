using System;
using System.Runtime.InteropServices;
using Apogee.Native.Zstd;
using Xunit;
using ZstdApi = Apogee.Native.Zstd.Zstd;

namespace Apogee.Native.Tests;

public unsafe class ZstdSmokeTests
{
    private static void Require() => NativeRequirements.Require(typeof(ZstdApi).Assembly, "zstd");

    /// <summary>Bytes that compress well but are not all the same: a counter with a slow drift.</summary>
    private static byte[] Sample(int length)
    {
        byte[] data = new byte[length];
        var random = new Random(7);
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)((i % 251) ^ (random.Next(16) == 0 ? random.Next(256) : 0));
        }

        return data;
    }

    private static byte[] Compress(byte[] data, int level)
    {
        byte[] output = new byte[(int)ZstdApi.CompressBound((nuint)data.Length)];
        fixed (byte* source = data)
        fixed (byte* target = output)
        {
            nuint size = ZstdApi.Compress(target, (nuint)output.Length, source, (nuint)data.Length, level);
            Assert.Equal(0u, ZstdApi.IsError(size));
            return output.AsSpan(0, (int)size).ToArray();
        }
    }

    private static byte[] Decompress(byte[] compressed, int length)
    {
        byte[] output = new byte[length];
        fixed (byte* source = compressed)
        fixed (byte* target = output)
        {
            nuint size = ZstdApi.Decompress(target, (nuint)output.Length, source, (nuint)compressed.Length);
            Assert.True(ZstdApi.IsError(size) == 0, Marshal.PtrToStringUTF8((IntPtr)ZstdApi.GetErrorName(size)));
            Assert.Equal((nuint)length, size);
        }

        return output;
    }

    [Fact]
    public void ZstdIsTheVendoredVersion()
    {
        Require();

        Assert.Equal(10507u, ZstdApi.VersionNumber());
        Assert.Equal("1.5.7", Marshal.PtrToStringUTF8((IntPtr)ZstdApi.VersionString()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(19)]
    public void ZstdRoundTripsAtEveryLevel(int level)
    {
        Require();

        byte[] data = Sample(300_000);
        byte[] compressed = Compress(data, level);

        Assert.True(compressed.Length < data.Length / 2, $"{compressed.Length} bytes for {data.Length}");
        fixed (byte* frame = compressed)
        {
            Assert.Equal((ulong)data.Length, ZstdApi.GetFrameContentSize(frame, (nuint)compressed.Length));
        }

        Assert.Equal(data, Decompress(compressed, data.Length));
    }

    [Fact]
    public void ZstdSaysWhenSomethingIsNotAFrame()
    {
        Require();

        byte[] garbage = new byte[64];
        fixed (byte* data = garbage)
        {
            Assert.Equal(ZstdApi.ContentSizeError, ZstdApi.GetFrameContentSize(data, (nuint)garbage.Length));

            byte[] output = new byte[64];
            fixed (byte* target = output)
            {
                nuint result = ZstdApi.Decompress(target, (nuint)output.Length, data, (nuint)garbage.Length);
                Assert.Equal(1u, ZstdApi.IsError(result));
                Assert.Equal(ErrorCode.PrefixUnknown, ZstdApi.GetErrorCode(result));
                Assert.Equal("Unknown frame descriptor", Marshal.PtrToStringUTF8((IntPtr)ZstdApi.GetErrorName(result)));
            }
        }
    }

    [Fact]
    public void ZstdCompressesInStreamsOnWorkerThreads()
    {
        Require();

        // Multithreaded compression is a build option of the library: asking for workers is an error without it.
        byte[] data = Sample(6 * 1024 * 1024);
        CCtx* context = ZstdApi.CreateCCtx();
        byte[] compressed = new byte[(int)ZstdApi.CompressBound((nuint)data.Length)];
        nuint written = 0;
        try
        {
            Assert.Equal(0u, ZstdApi.IsError(ZstdApi.CCtxSetParameter(context, CParameter.CompressionLevel, 3)));
            Assert.Equal(0u, ZstdApi.IsError(ZstdApi.CCtxSetParameter(context, CParameter.NbWorkers, 2)));
            Assert.Equal(0u, ZstdApi.IsError(ZstdApi.CCtxSetParameter(context, CParameter.ChecksumFlag, 1)));

            fixed (byte* source = data)
            fixed (byte* target = compressed)
            {
                var input = new InBuffer { Src = source, Size = (nuint)data.Length, Pos = 0 };
                var output = new OutBuffer { Dst = target, Size = (nuint)compressed.Length, Pos = 0 };
                nuint remaining;
                do
                {
                    remaining = ZstdApi.CompressStream2(context, &output, &input, EndDirective.End);
                    Assert.Equal(0u, ZstdApi.IsError(remaining));
                }
                while (remaining != 0);

                written = output.Pos;
            }
        }
        finally
        {
            ZstdApi.FreeCCtx(context);
        }

        Assert.Equal(data, Decompress(compressed.AsSpan(0, (int)written).ToArray(), data.Length));
    }

    [Fact]
    public void ZstdDecompressesInStreams()
    {
        Require();

        byte[] data = Sample(1_000_000);
        byte[] compressed = Compress(data, 5);
        byte[] restored = new byte[data.Length];
        DCtx* context = ZstdApi.CreateDCtx();
        try
        {
            fixed (byte* source = compressed)
            fixed (byte* target = restored)
            {
                var input = new InBuffer { Src = source, Size = (nuint)compressed.Length, Pos = 0 };
                var output = new OutBuffer { Dst = target, Size = 4096, Pos = 0 };
                nuint hint;
                do
                {
                    // A small window of the output at a time, as a streaming reader has it.
                    output.Size = Math.Min(output.Pos + 4096, (nuint)restored.Length);
                    hint = ZstdApi.DecompressStream(context, &output, &input);
                    Assert.Equal(0u, ZstdApi.IsError(hint));
                }
                while (hint != 0);

                Assert.Equal((nuint)data.Length, output.Pos);
            }
        }
        finally
        {
            ZstdApi.FreeDCtx(context);
        }

        Assert.Equal(data, restored);
    }

    [Fact]
    public void ZstdUsesADictionaryTheSenderAndReceiverShare()
    {
        Require();

        // Any content works as a raw dictionary; a frame made with one needs it to decompress.
        byte[] dictionary = Sample(8192);
        byte[] data = dictionary.AsSpan(1000, 3000).ToArray();
        byte[] compressed = new byte[(int)ZstdApi.CompressBound((nuint)data.Length)];
        CCtx* compressor = ZstdApi.CreateCCtx();
        DCtx* decompressor = ZstdApi.CreateDCtx();
        fixed (byte* dictionaryData = dictionary)
        {
            CDict* cdict = ZstdApi.CreateCDict(dictionaryData, (nuint)dictionary.Length, 3);
            DDict* ddict = ZstdApi.CreateDDict(dictionaryData, (nuint)dictionary.Length);
            try
            {
                nuint size;
                fixed (byte* source = data)
                fixed (byte* target = compressed)
                {
                    size = ZstdApi.CompressUsingCDict(compressor, target, (nuint)compressed.Length, source, (nuint)data.Length, cdict);
                    Assert.Equal(0u, ZstdApi.IsError(size));
                }

                Assert.True(size < 100, $"{size} bytes: the dictionary has the whole content");

                byte[] restored = new byte[data.Length];
                fixed (byte* source = compressed)
                fixed (byte* target = restored)
                {
                    nuint result = ZstdApi.DecompressUsingDDict(decompressor, target, (nuint)restored.Length, source, size, ddict);
                    Assert.Equal((nuint)data.Length, result);
                }

                Assert.Equal(data, restored);
            }
            finally
            {
                ZstdApi.FreeCDict(cdict);
                ZstdApi.FreeDDict(ddict);
                ZstdApi.FreeCCtx(compressor);
                ZstdApi.FreeDCtx(decompressor);
            }
        }
    }
}
