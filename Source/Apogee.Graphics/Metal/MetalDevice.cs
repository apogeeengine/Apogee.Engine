using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Core.Graphics;
using Apogee.Core.Logging;
using Apogee.Graphics.Shaders;
using Apogee.Interop;
using Apogee.Native.Metal;
using Apogee.Platform;
using MtlBuffer = Apogee.Native.Metal.Buffer;
using MtlDevice = Apogee.Native.Metal.Device;
using MtlLibrary = Apogee.Native.Metal.Library;
using MtlQueue = Apogee.Native.Metal.Queue;
using MtlTexture = Apogee.Native.Metal.Texture;

namespace Apogee.Graphics.Metal;

/// <summary>
/// The Metal implementation of <see cref="GraphicsDevice"/> (macOS 15 and later, on a GPU with Metal 3 and tier 2 argument buffers: Apple silicon and the Intel Macs of 2017 and later). It owns one command
/// queue and a residency set: everything the GPU reaches through an address or an argument buffer must be resident, so every buffer and texture is added to the set when it is made.
/// </summary>
public sealed unsafe partial class MetalDevice : GraphicsDevice
{
    private const string LogCategory = "Graphics";
    private const int MinimumMacOS = 15;

    private readonly MtlDevice* _device;
    private readonly MtlQueue* _queue;
    private readonly ResidencySet* _residency;
    private readonly DeviceInfo _info;
    private readonly Window? _window;
    private readonly object _residencyLock = new();
    private bool _residencyDirty;

    private MetalDevice(MtlDevice* device, in DeviceInfo info, Window? window)
    {
        _device = device;
        _info = info;
        _window = window;
        _queue = Mtl.QueueCreate(device);
        _residency = Mtl.ResidencySetCreate(device, _queue);
        if (_queue is null || _residency is null)
        {
            throw new MetalException("The Metal device could not make a command queue and its residency set.");
        }
    }

    internal MtlDevice* Handle => _device;

    internal MtlQueue* Queue => _queue;

    /// <summary>The bindless heap as a Metal one: the argument buffer.</summary>
    public MetalBindlessHeap MetalBindless => (MetalBindlessHeap)Bindless;

    public override GraphicsBackend Backend => GraphicsBackend.Metal;

    public override ShaderBackend ShaderBackend => ShaderBackend.Msl;

    public override string Name => NameOf(_info);

    public override GpuKind Kind => KindOf(_info);

    // GPU timings need counter sample buffers, which come with the frame loop's polish; until then the timings are empty.
    public override float TimestampPeriod => 1f;

    public override bool SupportsTimestamps => false;

    /// <summary>Constant buffers are bound at offsets that are a multiple of this on macOS.</summary>
    public override ulong MinUniformBufferOffsetAlignment => 256;

    public override float MaxSamplerAnisotropy => 16f;

    public override bool SupportsBc => _info.SupportsBcTextures != 0;

    public override bool SupportsAstc => _info.SupportsAppleFamily != 0;

    public override bool SupportsAnisotropy => true;

    // Metal's validation layer writes to the process's error output; there is no API to read it.
    public override int ValidationErrorCount => 0;

    public override string[] TakeValidationErrors() => [];

    // ---- Choosing and creating a device ------------------------------------------------------------------------------------

    private static string NameOf(in DeviceInfo info)
    {
        fixed (byte* name = info.Name)
        {
            return Utf8.FromPointer(name) ?? "Metal GPU";
        }
    }

    private static GpuKind KindOf(in DeviceInfo info) =>
        info.IsRemovable != 0 ? GpuKind.Discrete
        : info.HasUnifiedMemory != 0 ? GpuKind.Integrated
        : info.IsLowPower != 0 ? GpuKind.Integrated
        : GpuKind.Discrete;

    private static string OsVersion()
    {
        int major, minor, patch;
        Mtl.OsVersion(&major, &minor, &patch);
        return $"macOS {major}.{minor}.{patch}";
    }

    /// <summary>Why a GPU cannot be used; empty when it can.</summary>
    private static string[] ReasonsOf(in DeviceInfo info)
    {
        int major, minor, patch;
        Mtl.OsVersion(&major, &minor, &patch);
        var reasons = new List<string>();
        if (major < MinimumMacOS)
        {
            reasons.Add($"macOS {MinimumMacOS} or later (residency sets), this is {major}.{minor}");
        }

        if (info.SupportsMetal3 == 0)
        {
            reasons.Add("Metal 3");
        }

        if (info.ArgumentBuffersTier < 1)
        {
            reasons.Add("tier 2 argument buffers");
        }

        if (info.SupportsResidencySets == 0)
        {
            reasons.Add("residency sets");
        }

        return [.. reasons];
    }

    private static List<(int Index, DeviceInfo Info)> Gpus()
    {
        var gpus = new List<(int, DeviceInfo)>();
        int count = Mtl.DeviceCount();
        for (int i = 0; i < count; i++)
        {
            MtlDevice* device = Mtl.DeviceGet(i);
            if (device is null)
            {
                continue;
            }

            DeviceInfo info;
            Mtl.DeviceInfo(device, &info);
            Mtl.Release(device);
            gpus.Add((i, info));
        }

        return gpus;
    }

    /// <summary>The GPUs of the system, for <see cref="GraphicsDevice.EnumerateDevices"/>.</summary>
    internal static GpuInfo[] EnumerateGpus() =>
        [.. Gpus().Select(g => new GpuInfo(NameOf(g.Info), KindOf(g.Info), $"Metal 3, {OsVersion()}", ReasonsOf(g.Info)))];

    internal static bool TryCreateFor(GraphicsDeviceOptions options, out GraphicsDevice? device, out string? error)
    {
        try
        {
            device = CreateFor(options);
            error = null;
            return true;
        }
        catch (Exception e) when (e is GraphicsException or DllNotFoundException or EntryPointNotFoundException)
        {
            device = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>Creates the device on the best usable GPU. Throws with every GPU's reason when none qualifies.</summary>
    internal static MetalDevice CreateFor(GraphicsDeviceOptions options)
    {
        string? preferred = options.PreferredDeviceName ?? Environment.GetEnvironmentVariable("APOGEE_GPU");
        List<(int Index, DeviceInfo Info)> gpus = Gpus();
        foreach ((int _, DeviceInfo info) in gpus)
        {
            Log.Info(LogCategory, $"GPU: {NameOf(info)} ({KindOf(info)}, Metal 3, {OsVersion()}){(ReasonsOf(info).Length == 0 ? "" : " unusable: " + string.Join("; ", ReasonsOf(info)))}");
        }

        (int Index, DeviceInfo Info)? chosen = gpus
            .Where(g => ReasonsOf(g.Info).Length == 0)
            .OrderByDescending(g => preferred is not null && NameOf(g.Info).Contains(preferred, StringComparison.OrdinalIgnoreCase))
            .ThenBy(g => KindOf(g.Info))
            .Select(g => ((int, DeviceInfo)?)g)
            .FirstOrDefault();
        if (chosen is null)
        {
            throw new MetalException(gpus.Count == 0
                ? "No Metal GPU found."
                : $"No GPU meets the engine's Metal baseline (macOS {MinimumMacOS}, Metal 3, tier 2 argument buffers): "
                    + string.Join(" | ", gpus.Select(g => $"{NameOf(g.Info)} needs {string.Join(", ", ReasonsOf(g.Info))}")));
        }

        MtlDevice* handle = Mtl.DeviceGet(chosen.Value.Index);
        var device = new MetalDevice(handle, chosen.Value.Info, options.Window);
        Log.Info(LogCategory, $"Using {device.Name}");
        try
        {
            device.Immediate = new MetalImmediateContext(device);
            var bindless = new MetalBindlessHeap(device);
            device.Bindless = bindless;
            bindless.CreateDefaults();
        }
        catch
        {
            device.Dispose();
            throw;
        }

        return device;
    }

    // ---- Residency ----------------------------------------------------------------------------------------------------------

    internal void Track(MtlBuffer* buffer)
    {
        lock (_residencyLock)
        {
            Mtl.ResidencySetAddBuffer(_residency, buffer);
            _residencyDirty = true;
        }
    }

    internal void Track(MtlTexture* texture)
    {
        lock (_residencyLock)
        {
            Mtl.ResidencySetAddTexture(_residency, texture);
            _residencyDirty = true;
        }
    }

    internal void Untrack(MtlBuffer* buffer)
    {
        lock (_residencyLock)
        {
            Mtl.ResidencySetRemoveBuffer(_residency, buffer);
            _residencyDirty = true;
        }
    }

    internal void Untrack(MtlTexture* texture)
    {
        lock (_residencyLock)
        {
            Mtl.ResidencySetRemoveTexture(_residency, texture);
            _residencyDirty = true;
        }
    }

    /// <summary>Makes the resources added and removed since the last commit resident (or not). Called before every command buffer is committed.</summary>
    internal void CommitResidency()
    {
        lock (_residencyLock)
        {
            if (_residencyDirty)
            {
                Mtl.ResidencySetCommit(_residency);
                _residencyDirty = false;
            }
        }
    }

    // ---- Resources -----------------------------------------------------------------------------------------------------------

    public override GpuBuffer CreateBuffer(ulong size, BufferUsage usage, MemoryKind kind = MemoryKind.DeviceLocal, string? name = null) => new MetalBuffer(this, size, usage, kind, name);

    public override GpuImage CreateImage(in ImageDescription description, string? name = null) => new MetalImage(this, description, name);

    public override GpuSampler CreateSampler(in SamplerDescription description, string? name = null) => new MetalSampler(this, description);

    private MtlLibrary* Library(in ShaderCode shader, string? name)
    {
        if (shader.Backend != ShaderBackend.Msl)
        {
            throw new ArgumentException($"Metal runs Metal shading language, not {shader.Backend} code.", nameof(shader));
        }

        byte* error = stackalloc byte[2048];
        error[0] = 0;
        fixed (byte* source = shader.Code.Append((byte)0).ToArray())
        {
            MtlLibrary* library = Mtl.LibraryCreate(_device, source, error, 2048);
            if (library is null)
            {
                throw new MetalException($"Metal could not compile the shader '{name ?? shader.EntryPoint}': {Utf8.FromPointer(error)}");
            }

            return library;
        }
    }

    public override Pipeline CreateComputePipeline(in ShaderCode shader, string? name = null)
    {
        ThreadGroupSize group = shader.Group ?? throw new ArgumentException($"The compute shader '{name ?? shader.EntryPoint}' does not say its thread group size.", nameof(shader));
        MtlLibrary* library = Library(shader, name);
        try
        {
            byte* error = stackalloc byte[2048];
            error[0] = 0;
            fixed (byte* entry = Utf8.ToZeroTerminated(shader.EntryPoint))
            {
                ComputePipeline* pipeline = Mtl.ComputePipelineCreate(_device, library, entry, error, 2048);
                return pipeline is null
                    ? throw new MetalException($"Metal could not make the compute pipeline '{name ?? shader.EntryPoint}': {Utf8.FromPointer(error)}")
                    : new MetalPipeline(pipeline, group);
            }
        }
        finally
        {
            Mtl.Release(library);
        }
    }

    public override Pipeline CreateGraphicsPipeline(GraphicsPipelineDescription description, string? name = null)
    {
        MtlLibrary* vertexLibrary = Library(description.Vertex, name);
        MtlLibrary* fragmentLibrary = description.Fragment is { } fragment ? Library(fragment, name) : null;
        try
        {
            var desc = new RenderPipelineDesc
            {
                ColorCount = description.ColorFormats.Length,
                DepthFormat = Mtl.PixelFormat((int)description.DepthFormat),
                StencilFormat = FormatInfo.HasStencil(description.DepthFormat) ? Mtl.PixelFormat((int)description.DepthFormat) : 0,
                Blend = (int)description.Blend,
                SampleCount = 1,
                TopologyClass = description.Topology switch
                {
                    PrimitiveTopology.PointList => 1,
                    PrimitiveTopology.LineList or PrimitiveTopology.LineStrip => 2,
                    _ => 3,
                },
            };
            for (int i = 0; i < description.ColorFormats.Length && i < 8; i++)
            {
                desc.ColorFormats[i] = Mtl.PixelFormat((int)description.ColorFormats[i]);
            }

            byte* error = stackalloc byte[2048];
            error[0] = 0;
            RenderPipeline* pipeline;
            fixed (byte* vertexEntry = Utf8.ToZeroTerminated(description.Vertex.EntryPoint))
            fixed (byte* fragmentEntry = Utf8.ToZeroTerminated(description.Fragment?.EntryPoint ?? ""))
            {
                pipeline = Mtl.RenderPipelineCreate(_device, vertexLibrary, vertexEntry, fragmentLibrary, description.Fragment is null ? null : fragmentEntry, &desc, error, 2048);
            }

            if (pipeline is null)
            {
                throw new MetalException($"Metal could not make the render pipeline '{name}': {Utf8.FromPointer(error)}");
            }

            // Without a depth test the depth state always passes and never writes (a pipeline with a depth attachment still needs a state).
            DepthStencil* depth = Mtl.DepthStencilCreate(_device, description.DepthTest ? (int)description.DepthCompare : (int)CompareOp.Always, description.DepthTest && description.DepthWrite ? 1 : 0);
            return new MetalPipeline(pipeline, depth, (int)description.CullMode, description.FrontFace == FrontFace.CounterClockwise, (int)description.Topology);
        }
        finally
        {
            Mtl.Release(vertexLibrary);
            if (fragmentLibrary is not null)
            {
                Mtl.Release(fragmentLibrary);
            }
        }
    }

    public override Swapchain CreateSwapchain(bool vsync = true)
    {
        Window window = _window ?? throw new InvalidOperationException("The device was created without a window, so it cannot present.");
        return new MetalSwapchain(this, window, vsync);
    }

    public override FrameLoop CreateFrameLoop(Swapchain? swapchain = null, int framesInFlight = 2, int arenaBytes = 4 * 1024 * 1024) => new MetalFrameLoop(this, swapchain, framesInFlight, arenaBytes);

    // ---- Lifetime ------------------------------------------------------------------------------------------------------------

    public override void WaitIdle()
    {
        // Command buffers of a queue run in order, so when an empty one is finished so are all before it.
        CommandBuffer* commandBuffer = Mtl.CommandBufferCreate(_queue);
        Mtl.CommandBufferCommit(commandBuffer);
        Mtl.CommandBufferWait(commandBuffer);
        Mtl.Release(commandBuffer);
    }

    public override void Dispose()
    {
        if (_queue is not null && Immediate is not null)
        {
            WaitIdle();
        }

        (Bindless as MetalBindlessHeap)?.Dispose();
        Immediate?.Dispose();
        if (_residency is not null)
        {
            Mtl.Release(_residency);
        }

        if (_queue is not null)
        {
            Mtl.Release(_queue);
        }

        if (_device is not null)
        {
            Mtl.Release(_device);
        }
    }
}
