using System;

namespace Apogee.Audio;

/// <summary>An EAX reverb for OpenAL Soft's effect (<c>AL_EFFECT_EAXREVERB</c>), in the units of the EFX extension: times in seconds, gains as factors.</summary>
internal readonly record struct EfxReverb(
    float Density,
    float Diffusion,
    float Gain,
    float GainHf,
    float DecayTime,
    float DecayHfRatio,
    float ReflectionsGain,
    float ReflectionsDelay,
    float LateReverbGain,
    float LateReverbDelay,
    float AirAbsorptionGainHf,
    float RoomRolloffFactor,
    float WetGain);

/// <summary>The parameters of each <see cref="ReverbPreset"/>, our own approximations of the usual spaces.</summary>
internal static class ReverbPresets
{
    /// <summary>The reverb of a preset. <paramref name="levelOffset"/> (decibels) moves its wet level.</summary>
    public static EfxReverb Of(ReverbPreset preset, float levelOffset)
    {
        //                                   decay s  early s  late s  hfDecay  diffusion density  highCut Hz  early/late mix %  wet dB
        (float decay, float early, float late, float hfDecay, float diffusion, float density, float highCut, float mix, float wet) p = preset switch
        {
            ReverbPreset.Generic => (1.5f, 0.007f, 0.011f, 0.83f, 1.00f, 1.0f, 14500, 96, -8),
            ReverbPreset.Room => (0.4f, 0.002f, 0.003f, 0.83f, 1.00f, 1.0f, 10000, 88, -6),
            ReverbPreset.Hallway => (1.5f, 0.007f, 0.011f, 0.59f, 1.00f, 1.0f, 7800, 87, -6),
            ReverbPreset.Garage => (0.9f, 0.001f, 0.002f, 0.70f, 1.00f, 1.0f, 9000, 90, -5),
            ReverbPreset.Barn => (2.2f, 0.012f, 0.017f, 0.50f, 1.00f, 0.9f, 6000, 85, -5),
            ReverbPreset.Hangar => (10.0f, 0.020f, 0.030f, 0.23f, 1.00f, 1.0f, 7000, 80, -4),
            ReverbPreset.Cave => (2.9f, 0.015f, 0.022f, 1.00f, 1.00f, 1.0f, 20000, 76, -3),
            ReverbPreset.Forest => (1.5f, 0.162f, 0.088f, 0.54f, 0.79f, 1.0f, 7500, 70, -10),
            ReverbPreset.Plain => (1.5f, 0.179f, 0.100f, 0.21f, 0.50f, 1.0f, 7000, 60, -12),
            _ => default,
        };

        if (preset == ReverbPreset.Off)
        {
            return new EfxReverb(1, 1, 0.32f, 1, 1, 1, 0.05f, 0.007f, 1.26f, 0.011f, 0.994f, 0, 0);
        }

        float lateShare = p.mix / 100f;
        return new EfxReverb(
            Density: p.density,
            Diffusion: p.diffusion,
            Gain: 0.32f,
            GainHf: Math.Clamp(p.highCut / 16000f, 0.1f, 1f),
            DecayTime: Math.Clamp(p.decay, 0.1f, 20f),
            DecayHfRatio: Math.Clamp(p.hfDecay, 0.1f, 2f),
            ReflectionsGain: 0.05f + (0.9f * (1 - lateShare)),
            ReflectionsDelay: Math.Min(p.early, 0.3f),
            LateReverbGain: 1.26f * lateShare,
            LateReverbDelay: Math.Min(p.late, 0.1f),
            AirAbsorptionGainHf: 0.994f,
            RoomRolloffFactor: 0,
            WetGain: Math.Clamp(MathF.Pow(10, (p.wet + levelOffset) / 20f), 0f, 1f));
    }
}
