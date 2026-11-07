using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apogee.Native.RmlUi;

namespace Apogee.UI;

/// <summary>A filter RmlUi compiled (a blur, a drop shadow, a color transform), or a mask image: kept here so that it exists whether or not a renderer does.</summary>
internal sealed record UiFilter(ArFilterKind Kind, float Sigma, Vector2 Offset, Vector4 Color, float[] Matrix, int MaskLayer = -1);

/// <summary>A gradient RmlUi compiled: the shape and the stops.</summary>
internal sealed record UiShader(ArShaderKind Kind, Vector2 P0, Vector2 P1, Vector2 Center, Vector2 Radius, float Angle, bool Repeating, (Vector4 Color, float Position)[] Stops);

public sealed unsafe partial class UiSystem
{
    private readonly Dictionary<ulong, UiFilter> _filters = [];
    private readonly Dictionary<ulong, UiShader> _shaders = [];
    private ulong _nextEffect = 1;
    private ulong _nextLayer = 1;

    internal UiFilter? FilterOf(ulong handle) => _filters.GetValueOrDefault(handle);

    internal UiShader? ShaderOf(ulong handle) => _shaders.GetValueOrDefault(handle);

    internal ulong AddFilter(UiFilter filter)
    {
        ulong handle = _nextEffect++;
        _filters[handle] = filter;
        return handle;
    }

    // ---- Host functions for effects ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostEnableClipMask(void* user, int enable) => s_active?._renderer?.EnableClipMask(enable != 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostRenderToClipMask(void* user, int operation, ulong geometry, float x, float y) => s_active?._renderer?.RenderToClipMask(operation, geometry, x, y);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong HostPushLayer(void* user) => s_active is { } ui ? ui._renderer?.PushLayer() ?? ui._nextLayer++ : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostCompositeLayers(void* user, ulong source, ulong destination, int blendMode, ulong* filters, int filterCount) =>
        s_active?._renderer?.CompositeLayers(source, destination, blendMode != 0, new ReadOnlySpan<ulong>(filters, filterCount));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostPopLayer(void* user) => s_active?._renderer?.PopLayer();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong HostSaveLayerAsTexture(void* user) => s_active?._renderer?.SaveLayerAsTexture() ?? 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong HostSaveLayerAsMaskImage(void* user) => s_active?._renderer?.SaveLayerAsMaskImage() ?? 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong HostCompileFilter(void* user, ArFilterDesc* desc)
    {
        if (s_active is not { } ui)
        {
            return 0;
        }

        var kind = (ArFilterKind)desc->Kind;
        float[] matrix = new float[20];
        for (int i = 0; i < 20; i++)
        {
            matrix[i] = desc->Matrix[i];
        }

        var color = new Vector4(desc->Color[0], desc->Color[1], desc->Color[2], desc->Color[3]) / 255f;
        return ui.AddFilter(new UiFilter(kind, desc->Sigma, new Vector2(desc->OffsetX, desc->OffsetY), color, matrix));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostReleaseFilter(void* user, ulong filter) => s_active?._filters.Remove(filter);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong HostCompileShader(void* user, ArShaderDesc* desc)
    {
        if (s_active is not { } ui)
        {
            return 0;
        }

        var stops = new (Vector4, float)[desc->StopCount];
        for (int i = 0; i < stops.Length; i++)
        {
            ArColorStop stop = desc->Stops[i];
            stops[i] = (new Vector4(stop.R, stop.G, stop.B, stop.A) / 255f, stop.Position);
        }

        ulong handle = ui._nextEffect++;
        ui._shaders[handle] = new UiShader((ArShaderKind)desc->Kind, new Vector2(desc->P0x, desc->P0y), new Vector2(desc->P1x, desc->P1y), new Vector2(desc->CenterX, desc->CenterY),
            new Vector2(desc->RadiusX, desc->RadiusY), desc->Angle, desc->Repeating != 0, stops);
        return handle;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostRenderShader(void* user, ulong shader, ulong geometry, float x, float y, ulong texture) => s_active?._renderer?.RenderShader(shader, geometry, x, y, texture);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostReleaseShader(void* user, ulong shader) => s_active?._shaders.Remove(shader);
}
