namespace Apogee.Audio;

/// <summary>Where the mixed sound goes.</summary>
public enum AudioOutput
{
    /// <summary>The platform's default device, in real time.</summary>
    Default,

    /// <summary>
    /// Nowhere, but in the game's time: <see cref="AudioSystem.Update"/> mixes the seconds it is given and drops the samples, so sounds start, loop and end exactly as they would be heard and no sound
    /// card is needed (headless runs, script tests, the editor when it is not playing).
    /// </summary>
    None,

    /// <summary>A WAV file, mixed in the game's time like <see cref="None"/>: what the tests record and analyse. Closed when the audio system is disposed.</summary>
    WavFile,
}

/// <summary>How an <see cref="AudioSystem"/> is made.</summary>
public sealed class AudioOptions
{
    public AudioOutput Output { get; init; } = AudioOutput.Default;

    /// <summary>The WAV file for <see cref="AudioOutput.WavFile"/>.</summary>
    public string? WavPath { get; init; }

    /// <summary>The mixing rate. A device that cannot do it mixes at its own.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Scales the Doppler effect (1 is physical).</summary>
    public float DopplerScale { get; init; } = 1;

    /// <summary>Game units per meter. The world is in meters, so one.</summary>
    public float DistanceFactor { get; init; } = 1;

    /// <summary>Scales how fast sound fades with distance (1 is the inverse-distance rolloff).</summary>
    public float RolloffScale { get; init; } = 1;
}
