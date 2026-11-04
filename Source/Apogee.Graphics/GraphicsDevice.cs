using System;
using System.Collections.Generic;
using Apogee.Core.Graphics;
using Apogee.Graphics.Shaders;
using Apogee.Graphics.Metal;
using Apogee.Graphics.Vulkan;
using Apogee.Platform;

namespace Apogee.Graphics;

/// <summary>The graphics API a device runs on.</summary>
public enum GraphicsBackend
{
    Vulkan,
    Metal,
}

public enum GpuKind
{
    Discrete,
    Integrated,
    Virtual,
    Software,
    Other,
}

/// <summary>A GPU a backend found, and whether the engine can use it.</summary>
/// <param name="Reasons">Why it cannot be used (missing features, no queue that can present); empty when it can.</param>
public sealed record GpuInfo(string Name, GpuKind Kind, string Api, string[] Reasons)
{
    public bool Suitable => Reasons.Length == 0;

    public override string ToString() => $"{Name} ({Kind}, {Api}){(Suitable ? "" : " unusable: " + string.Join("; ", Reasons))}";
}

public sealed class GraphicsDeviceOptions
{
    /// <summary>The API to use. Null: the platform's (<see cref="GraphicsDevice.DefaultBackend"/>); <c>APOGEE_GRAPHICS=vulkan|metal</c> chooses for development.</summary>
    public GraphicsBackend? Backend { get; set; }

    /// <summary>A window the device has to be able to present to; create it with <see cref="GraphicsDevice.RequiredWindowOptions"/>. Null for a headless device.</summary>
    public Window? Window { get; set; }

    public string ApplicationName { get; set; } = "Apogee";

    /// <summary>Picks the device whose name contains this (case-insensitive) when it is usable, before the usual preference for discrete GPUs. <c>APOGEE_GPU</c> sets it too.</summary>
    public string? PreferredDeviceName { get; set; }
}

/// <summary>
/// The logical device of one graphics API and everything created with it. The renderer talks to this class and the abstract classes it hands out (buffers, images, pipelines, a
/// command recorder, a swapchain and frame loop); <c>Apogee.Graphics.Vulkan</c> has the Vulkan implementation, and a Metal one follows. The engine's baseline is the same
/// on every API: dynamic rendering, explicit barriers, buffer device addresses and bindless textures; a GPU without them is rejected with the reason.
/// </summary>
public abstract unsafe class GraphicsDevice : IDisposable
{
    public abstract GraphicsBackend Backend { get; }

    /// <summary>The kind of shader code this device runs: pass it to <see cref="ShaderCode"/>.</summary>
    public abstract ShaderBackend ShaderBackend { get; }

    public abstract string Name { get; }

    public abstract GpuKind Kind { get; }

    /// <summary>Records, submits and waits: for loading and tools.</summary>
    public ImmediateContext Immediate { get; protected set; } = null!;

    /// <summary>The set of textures and samplers every pipeline sees, by index.</summary>
    public BindlessHeap Bindless { get; protected set; } = null!;

    /// <summary>Nanoseconds per timestamp tick, for GPU timings.</summary>
    public abstract float TimestampPeriod { get; }

    /// <summary>The queue writes timestamps (all of graphics and compute can).</summary>
    public abstract bool SupportsTimestamps { get; }

    public abstract ulong MinUniformBufferOffsetAlignment { get; }

    public abstract float MaxSamplerAnisotropy { get; }

    /// <summary>BC1-7 textures can be sampled (desktop GPUs); otherwise transcode Basis textures to ASTC or RGBA8.</summary>
    public abstract bool SupportsBc { get; }

    /// <summary>ASTC LDR textures can be sampled (mobile and Apple GPUs).</summary>
    public abstract bool SupportsAstc { get; }

    public abstract bool SupportsAnisotropy { get; }

    /// <summary>How many warnings and errors the API's validation layer has reported; tests fail on any.</summary>
    public abstract int ValidationErrorCount { get; }

    /// <summary>Takes the validation messages reported so far.</summary>
    public abstract string[] TakeValidationErrors();

    // ---- Choosing and creating a device ------------------------------------------------------------------------------------

    /// <summary>The API used when nothing is asked for: <c>APOGEE_GRAPHICS</c>, else the platform's (Metal on macOS, Vulkan elsewhere).</summary>
    public static GraphicsBackend DefaultBackend
    {
        get
        {
            string? chosen = Environment.GetEnvironmentVariable("APOGEE_GRAPHICS");
            if (chosen is not null && Enum.TryParse(chosen, ignoreCase: true, out GraphicsBackend backend))
            {
                return backend;
            }

            return OperatingSystem.IsMacOS() ? GraphicsBackend.Metal : GraphicsBackend.Vulkan;
        }
    }

    /// <summary>The flag a window needs to present to a device of this API (a window must be created for the API it will be drawn with).</summary>
    public static WindowOptions RequiredWindowOptions(GraphicsBackend? backend = null) => (backend ?? DefaultBackend) switch
    {
        GraphicsBackend.Vulkan => WindowOptions.Vulkan,
        GraphicsBackend.Metal => WindowOptions.Metal,
        _ => WindowOptions.None,
    };

    /// <summary>Creates the device on the best usable GPU. Throws with every GPU's reason when none qualifies.</summary>
    public static GraphicsDevice Create(GraphicsDeviceOptions? options = null)
    {
        options ??= new GraphicsDeviceOptions();
        return (options.Backend ?? DefaultBackend) switch
        {
            GraphicsBackend.Vulkan => VulkanDevice.CreateFor(options),
            GraphicsBackend.Metal => MetalDevice.CreateFor(options),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    /// <summary>Like <see cref="Create"/>, for a machine that may have no usable GPU or driver: says why instead of throwing.</summary>
    public static bool TryCreate(GraphicsDeviceOptions? options, out GraphicsDevice? device, out string? error)
    {
        options ??= new GraphicsDeviceOptions();
        switch (options.Backend ?? DefaultBackend)
        {
            case GraphicsBackend.Vulkan:
                return VulkanDevice.TryCreateFor(options, out device, out error);
            default:
                return MetalDevice.TryCreateFor(options, out device, out error);
        }
    }

    /// <summary>Lists the GPUs the API reports, with whether each meets the baseline.</summary>
    public static GpuInfo[] EnumerateDevices(GraphicsDeviceOptions? options = null)
    {
        options ??= new GraphicsDeviceOptions();
        return (options.Backend ?? DefaultBackend) switch
        {
            GraphicsBackend.Vulkan => VulkanDevice.EnumerateGpus(options),
            _ => EnumerateMetal(),
        };
    }

    private static GpuInfo[] EnumerateMetal()
    {
        try
        {
            return MetalDevice.EnumerateGpus();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return []; // not a Mac, or the Metal library is not there
        }
    }

    // ---- Resources -----------------------------------------------------------------------------------------------------------

    public abstract GpuBuffer CreateBuffer(ulong size, BufferUsage usage, MemoryKind kind = MemoryKind.DeviceLocal, string? name = null);

    public abstract GpuImage CreateImage(in ImageDescription description, string? name = null);

    public abstract GpuSampler CreateSampler(in SamplerDescription description, string? name = null);

    public abstract Pipeline CreateComputePipeline(in ShaderCode shader, string? name = null);

    public abstract Pipeline CreateGraphicsPipeline(GraphicsPipelineDescription description, string? name = null);

    /// <summary>A swapchain for the window the device was created for (<see cref="GraphicsDeviceOptions.Window"/>).</summary>
    public abstract Swapchain CreateSwapchain(bool vsync = true);

    /// <summary>The loop that records and submits frames, to <paramref name="swapchain"/> or headless.</summary>
    public abstract FrameLoop CreateFrameLoop(Swapchain? swapchain = null, int framesInFlight = 2, int arenaBytes = 4 * 1024 * 1024);

    public abstract void WaitIdle();

    public abstract void Dispose();

    /// <summary>Creates a device-local buffer holding <paramref name="data"/>.</summary>
    public GpuBuffer CreateBuffer(ReadOnlySpan<byte> data, BufferUsage usage, string? name = null)
    {
        GpuBuffer buffer = CreateBuffer((ulong)data.Length, usage, MemoryKind.DeviceLocal, name);
        UploadBuffer(buffer, data);
        return buffer;
    }

    /// <summary>Copies <paramref name="data"/> into a buffer (through a staging buffer when the buffer is on the GPU) and waits for it. For loading, not for every frame.</summary>
    public void UploadBuffer(GpuBuffer destination, ReadOnlySpan<byte> data, ulong offset = 0)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (destination.Mapped is not null)
        {
            destination.Write(data, offset);
            return;
        }

        ulong size = (ulong)data.Length;
        using GpuBuffer staging = CreateBuffer(size, BufferUsage.TransferSource, MemoryKind.Upload, "Upload staging");
        staging.Write(data);
        Immediate.Submit(recorder =>
        {
            recorder.CopyBuffer(staging, destination, size, 0, offset);
            recorder.Barriers([], [new BufferBarrier(destination, BufferState.TransferDestination, BufferState.ShaderRead)]);
        });
    }

    /// <summary>
    /// Fills subresources of a fresh image from packed texel data and leaves it in <paramref name="finalState"/>. The image must not have been used
    /// yet (its layout is undefined).
    /// </summary>
    public void UploadImage(GpuImage destination, ReadOnlySpan<byte> data, ReadOnlySpan<ImageRegion> regions, ImageState finalState)
    {
        using GpuBuffer staging = CreateBuffer((ulong)data.Length, BufferUsage.TransferSource, MemoryKind.Upload, "Image staging");
        staging.Write(data);

        ImageRegion[] copy = regions.ToArray();
        Immediate.Submit(recorder =>
        {
            recorder.Barrier(ImageBarrier.For(destination, ImageState.Undefined, ImageState.TransferDestination));
            recorder.CopyBufferToImage(staging, destination, copy);
            recorder.Barrier(ImageBarrier.For(destination, ImageState.TransferDestination, finalState));
        });
    }

    /// <summary>Copies a buffer's bytes back to the CPU (through a readback buffer when it is on the GPU) and waits.</summary>
    public byte[] ReadBuffer(GpuBuffer source, ulong offset, int length)
    {
        if (source.Mapped is not null)
        {
            return source.Read(offset, length);
        }

        using GpuBuffer readback = CreateBuffer((ulong)length, BufferUsage.TransferDestination, MemoryKind.Readback, "Readback");
        Immediate.Submit(recorder =>
        {
            recorder.Barriers([], [new BufferBarrier(source, BufferState.ShaderWrite, BufferState.TransferSource)]);
            recorder.CopyBuffer(source, readback, (ulong)length, offset, 0);
        });
        return readback.Read(0, length);
    }

    /// <summary>Copies one mip level of one layer of an image, which is in <paramref name="state"/>, back to the CPU as packed texels, and leaves it in that state.</summary>
    public byte[] ReadImage(GpuImage source, ImageState state, uint mipLevel = 0, uint layer = 0)
    {
        uint width = Math.Max(1, source.Width >> (int)mipLevel);
        uint height = Math.Max(1, source.Height >> (int)mipLevel);
        int size = checked((int)FormatInfo.LevelSize(source.Format, width, height));

        using GpuBuffer readback = CreateBuffer((ulong)size, BufferUsage.TransferDestination, MemoryKind.Readback, "Image readback");
        Immediate.Submit(recorder =>
        {
            recorder.Barrier(ImageBarrier.For(source, state, ImageState.TransferSource));
            recorder.CopyImageToBuffer(source, readback, [new ImageRegion(0, mipLevel, layer, width, height)]);
            recorder.Barrier(ImageBarrier.For(source, ImageState.TransferSource, state));
        });
        return readback.Read(0, size);
    }
}
