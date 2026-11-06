using System;
using System.IO;
using Apogee.Core.Logging;
using Apogee.Native.OpenAL;
using AL = Apogee.Native.OpenAL.OpenAL;

namespace Apogee.Audio;

// The device, the context, the render loop of a device that is not real, and the one reverb effect.
public sealed unsafe partial class AudioSystem
{
    private const int BlockFrames = 1024;

    private ALCdevice* _device;
    private ALCcontext* _context;
    private bool _offline;
    private int _rate;
    private double _renderCarry;
    private float[] _block = new float[BlockFrames * 2];
    private WavWriter? _wav;
    private uint _effect;
    private uint _slot;

    /// <summary>Whether reverb and occlusion work (OpenAL Soft's EFX extension).</summary>
    internal bool HasEfx => _slot != 0;

    internal uint ReverbSlot => _slot;

    private void OpenDevice()
    {
        _rate = _options.SampleRate;
        _offline = _options.Output != AudioOutput.Default;
        ALCdevice* device = _offline ? null : AL.AlcOpenDevice(null);
        if (!_offline && device == null)
        {
            Warn("There is no audio device: sounds are mixed into nowhere.");
            _offline = true;
        }

        if (_offline)
        {
            device = AL.AlcLoopbackOpenDeviceSOFT(null);
            if (device == null)
            {
                Warn("OpenAL could not open its loopback device: audio stays silent.");
                return;
            }
        }

        int[] attributes = _offline
            ? [AL.AlcFormatChannelsSoft, AL.AlcStereoSoft, AL.AlcFormatTypeSoft, AL.AlcFloatSoft, AL.AlcFrequency, _rate, AL.AlcMaxAuxiliarySends, 1, 0]
            : [AL.AlcFrequency, _rate, AL.AlcMaxAuxiliarySends, 1, 0];
        ALCcontext* context;
        fixed (int* a = attributes)
        {
            context = AL.AlcCreateContext(device, a);
        }

        if (context == null || AL.AlcMakeContextCurrent(context) == 0)
        {
            Warn("OpenAL could not start a context: audio stays silent.");
            if (context != null)
            {
                AL.AlcDestroyContext(context);
            }

            AL.AlcCloseDevice(device);
            return;
        }

        _device = device;
        _context = context;
        AL.AlDistanceModel(AL.AlInverseDistanceClamped);
        AL.AlDopplerFactor(_options.DopplerScale);
        AL.AlListenerf(AL.AlMetersPerUnit, 1f / Math.Max(_options.DistanceFactor, 1e-6f));
        AL.AlListenerf(AL.AlGain, _masterVolume);
        if (_options.Output == AudioOutput.WavFile && _options.WavPath is { } wavPath)
        {
            _wav = new WavWriter(wavPath, _rate);
        }

        CreateReverb();
        _ = AL.AlGetError(); // whatever the setup left
    }

    private void CreateReverb()
    {
        bool efx;
        fixed (byte* name = "ALC_EXT_EFX\0"u8)
        {
            efx = AL.AlcIsExtensionPresent(_device, name) != 0;
        }

        if (!efx)
        {
            Log.Warn("Audio", "This OpenAL has no EFX extension: no reverb zones and no occlusion.");
            return;
        }

        uint effect;
        uint slot;
        AL.AlGenEffects(1, &effect);
        AL.AlEffecti(effect, AL.AlEffectType, AL.AlEffectEaxreverb);
        AL.AlGenAuxiliaryEffectSlots(1, &slot);
        if (!Ok("make the reverb"))
        {
            AL.AlDeleteEffects(1, &effect);
            AL.AlDeleteAuxiliaryEffectSlots(1, &slot);
            return;
        }

        _effect = effect;
        _slot = slot;
        SetReverb(ReverbPresets.Of(ReverbPreset.Off, 0));
    }

    private void SetReverb(EfxReverb r)
    {
        if (_slot == 0)
        {
            return;
        }

        AL.AlEffectf(_effect, AL.AlEaxreverbDensity, r.Density);
        AL.AlEffectf(_effect, AL.AlEaxreverbDiffusion, r.Diffusion);
        AL.AlEffectf(_effect, AL.AlEaxreverbGain, r.Gain);
        AL.AlEffectf(_effect, AL.AlEaxreverbGainhf, r.GainHf);
        AL.AlEffectf(_effect, AL.AlEaxreverbDecayTime, r.DecayTime);
        AL.AlEffectf(_effect, AL.AlEaxreverbDecayHfratio, r.DecayHfRatio);
        AL.AlEffectf(_effect, AL.AlEaxreverbReflectionsGain, r.ReflectionsGain);
        AL.AlEffectf(_effect, AL.AlEaxreverbReflectionsDelay, r.ReflectionsDelay);
        AL.AlEffectf(_effect, AL.AlEaxreverbLateReverbGain, r.LateReverbGain);
        AL.AlEffectf(_effect, AL.AlEaxreverbLateReverbDelay, r.LateReverbDelay);
        AL.AlEffectf(_effect, AL.AlEaxreverbAirAbsorptionGainhf, r.AirAbsorptionGainHf);
        AL.AlEffectf(_effect, AL.AlEaxreverbRoomRolloffFactor, r.RoomRolloffFactor);
        AL.AlAuxiliaryEffectSloti(_slot, AL.AlEffectslotEffect, (int)_effect); // attached again so the new values apply
        AL.AlAuxiliaryEffectSlotf(_slot, AL.AlEffectslotGain, r.WetGain);
        Ok("set the reverb");
    }

    /// <summary>Mixes <paramref name="dt"/> seconds of the game into nowhere (or the WAV file), a block at a time with the streams topped up between blocks.</summary>
    private void Render(float dt)
    {
        _renderCarry += (double)dt * _rate;
        int frames = (int)_renderCarry;
        _renderCarry -= frames;
        frames = Math.Min(frames, _rate * 10); // a hitch of minutes is not worth mixing
        while (frames > 0)
        {
            int n = Math.Min(frames, BlockFrames);
            fixed (float* block = _block)
            {
                AL.AlcRenderSamplesSOFT(_device, block, n);
            }

            _wav?.Write(_block.AsSpan(0, n * 2));
            frames -= n;
            PumpStreams();
        }
    }

    internal uint MakeFilter()
    {
        if (!HasEfx)
        {
            return 0;
        }

        uint filter;
        AL.AlGenFilters(1, &filter);
        AL.AlFilteri(filter, AL.AlFilterType, AL.AlFilterLowpass);
        return Ok("make a filter") ? filter : 0;
    }

    private void CloseDevice()
    {
        if (_slot != 0)
        {
            uint slot = _slot;
            uint effect = _effect;
            AL.AlAuxiliaryEffectSloti(slot, AL.AlEffectslotEffect, AL.AlEffectNull);
            AL.AlDeleteAuxiliaryEffectSlots(1, &slot);
            AL.AlDeleteEffects(1, &effect);
            _slot = 0;
        }

        if (AL.AlcGetCurrentContext() == _context)
        {
            AL.AlcMakeContextCurrent(null);
        }

        AL.AlcDestroyContext(_context);
        AL.AlcCloseDevice(_device);
        _context = null;
        _device = null;
        _wav?.Dispose();
        _wav = null;
    }
}

/// <summary>Writes the mix as a 32-bit float stereo WAV file, whose sizes are filled in when it is closed.</summary>
internal sealed class WavWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly int _rate;
    private long _bytes;

    public WavWriter(string path, int rate)
    {
        _rate = rate;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _file.Write(new byte[44]);
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        _file.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleaved));
        _bytes += interleaved.Length * sizeof(float);
    }

    public void Dispose()
    {
        using var header = new MemoryStream(44);
        using var w = new BinaryWriter(header);
        w.Write("RIFF"u8);
        w.Write((int)(36 + _bytes));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)3); // IEEE float
        w.Write((short)2);
        w.Write(_rate);
        w.Write(_rate * 2 * sizeof(float));
        w.Write((short)(2 * sizeof(float)));
        w.Write((short)32);
        w.Write("data"u8);
        w.Write((int)_bytes);
        _file.Position = 0;
        _file.Write(header.ToArray());
        _file.Dispose();
    }
}
