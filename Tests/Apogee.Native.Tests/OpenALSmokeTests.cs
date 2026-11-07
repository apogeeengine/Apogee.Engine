using System;
using System.Runtime.InteropServices;
using Apogee.Native.OpenAL;
using Xunit;
using AL = Apogee.Native.OpenAL.OpenAL;

namespace Apogee.Native.Tests;

/// <summary>OpenAL Soft renders a sound on its loopback device, with no sound card, and has the EFX effects the audio system uses.</summary>
public unsafe class OpenALSmokeTests
{
    private static void Require() => NativeRequirements.Require(typeof(AL).Assembly, "openal");

    [Fact]
    public void ALoopbackDeviceRendersASource()
    {
        Require();
        ALCdevice* device = AL.AlcLoopbackOpenDeviceSOFT(null);
        Assert.True(device != null);
        try
        {
            int[] attributes =
            [
                AL.AlcFormatChannelsSoft, AL.AlcStereoSoft,
                AL.AlcFormatTypeSoft, AL.AlcFloatSoft,
                AL.AlcFrequency, 48000,
                0,
            ];
            Assert.Equal(AL.AlcTrue, AL.AlcIsRenderFormatSupportedSOFT(device, 48000, AL.AlcStereoSoft, AL.AlcFloatSoft));
            ALCcontext* context;
            fixed (int* a = attributes)
            {
                context = AL.AlcCreateContext(device, a);
            }

            Assert.True(context != null);
            Assert.Equal(AL.AlcTrue, AL.AlcMakeContextCurrent(context));

            // A second of a 1 kHz tone, mono 16-bit, played at the listener's right.
            short[] tone = new short[48000];
            for (int i = 0; i < tone.Length; i++)
            {
                tone[i] = (short)(12000 * Math.Sin(2 * Math.PI * 1000 * i / 48000));
            }

            uint buffer;
            uint source;
            AL.AlGenBuffers(1, &buffer);
            fixed (short* data = tone)
            {
                AL.AlBufferData(buffer, AL.AlFormatMono16, data, tone.Length * sizeof(short), 48000);
            }

            AL.AlGenSources(1, &source);
            AL.AlSourcei(source, AL.AlBuffer, (int)buffer);
            AL.AlSource3f(source, AL.AlPosition, 5, 0, 0);
            AL.AlSourcePlay(source);
            Assert.Equal(AL.AlNoError, AL.AlGetError());

            float[] mix = new float[2048 * 2];
            fixed (float* output = mix)
            {
                AL.AlcRenderSamplesSOFT(device, output, 2048);
            }

            double left = 0;
            double right = 0;
            for (int i = 0; i < 2048; i++)
            {
                left += mix[i * 2] * mix[i * 2];
                right += mix[i * 2 + 1] * mix[i * 2 + 1];
            }

            Assert.True(right > 0.01, "something was rendered");
            Assert.True(right > left * 4, $"a source on the right is louder on the right ({right} against {left})");

            AL.AlDeleteSources(1, &source);
            AL.AlDeleteBuffers(1, &buffer);
            AL.AlcMakeContextCurrent(null);
            AL.AlcDestroyContext(context);
        }
        finally
        {
            AL.AlcCloseDevice(device);
        }
    }

    [Fact]
    public void TheReverbAndFilterEffectsExist()
    {
        Require();
        ALCdevice* device = AL.AlcLoopbackOpenDeviceSOFT(null);
        Assert.True(device != null);
        try
        {
            int[] attributes = [AL.AlcFormatChannelsSoft, AL.AlcStereoSoft, AL.AlcFormatTypeSoft, AL.AlcFloatSoft, AL.AlcFrequency, 48000, 0];
            ALCcontext* context;
            fixed (int* a = attributes)
            {
                context = AL.AlcCreateContext(device, a);
            }

            AL.AlcMakeContextCurrent(context);
            byte[] name = "ALC_EXT_EFX\0"u8.ToArray();
            fixed (byte* n = name)
            {
                Assert.Equal(AL.AlcTrue, AL.AlcIsExtensionPresent(device, n));
            }

            uint effect;
            uint slot;
            uint filter;
            AL.AlGenEffects(1, &effect);
            AL.AlEffecti(effect, AL.AlEffectType, AL.AlEffectEaxreverb);
            Assert.Equal(AL.AlNoError, AL.AlGetError());
            AL.AlEffectf(effect, AL.AlEaxreverbDecayTime, 4.5f);
            AL.AlGenAuxiliaryEffectSlots(1, &slot);
            AL.AlAuxiliaryEffectSloti(slot, AL.AlEffectslotEffect, (int)effect);
            AL.AlGenFilters(1, &filter);
            AL.AlFilteri(filter, AL.AlFilterType, AL.AlFilterLowpass);
            AL.AlFilterf(filter, AL.AlLowpassGainhf, 0.2f);
            Assert.Equal(AL.AlNoError, AL.AlGetError());

            AL.AlDeleteFilters(1, &filter);
            AL.AlDeleteAuxiliaryEffectSlots(1, &slot);
            AL.AlDeleteEffects(1, &effect);
            AL.AlcMakeContextCurrent(null);
            AL.AlcDestroyContext(context);
        }
        finally
        {
            AL.AlcCloseDevice(device);
        }
    }
}
