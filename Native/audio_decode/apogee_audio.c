// The audio decoders behind one small API (apogee_audio.h). The libraries' implementations are compiled here, from memory only (no stdio).
#define DR_WAV_NO_STDIO
#define DR_WAV_IMPLEMENTATION
#include "dr_wav.h"
#define DR_FLAC_NO_STDIO
#define DR_FLAC_IMPLEMENTATION
#include "dr_flac.h"
#define DR_MP3_NO_STDIO
#define DR_MP3_IMPLEMENTATION
#include "dr_mp3.h"
#define STB_VORBIS_NO_STDIO
#define STB_VORBIS_NO_PUSHDATA_API
#include "stb_vorbis.c"

#include "apogee_audio.h"

#include <stdlib.h>
#include <string.h>

typedef enum Kind { KIND_WAV, KIND_FLAC, KIND_MP3, KIND_VORBIS } Kind;

struct AaDecoder
{
    Kind kind;
    uint32_t channels;
    union
    {
        drwav wav;
        drflac* flac;
        drmp3 mp3;
        stb_vorbis* vorbis;
    } u;
};

static int starts_with(const unsigned char* data, size_t size, const char* tag)
{
    size_t n = strlen(tag);
    return size >= n && memcmp(data, tag, n) == 0;
}

AaDecoder* aa_open(const void* data, size_t size, AaInfo* info)
{
    if (data == NULL || size < 4 || info == NULL)
        return NULL;
    const unsigned char* bytes = (const unsigned char*)data;
    AaDecoder* d = (AaDecoder*)calloc(1, sizeof(AaDecoder));
    if (d == NULL)
        return NULL;

    if (starts_with(bytes, size, "RIFF") || starts_with(bytes, size, "RF64"))
    {
        d->kind = KIND_WAV;
        if (!drwav_init_memory(&d->u.wav, data, size, NULL))
            goto fail;
        d->channels = d->u.wav.channels;
        info->channels = d->u.wav.channels;
        info->sample_rate = d->u.wav.sampleRate;
        info->frames = d->u.wav.totalPCMFrameCount;
    }
    else if (starts_with(bytes, size, "fLaC"))
    {
        d->kind = KIND_FLAC;
        d->u.flac = drflac_open_memory(data, size, NULL);
        if (d->u.flac == NULL)
            goto fail;
        d->channels = d->u.flac->channels;
        info->channels = d->u.flac->channels;
        info->sample_rate = d->u.flac->sampleRate;
        info->frames = d->u.flac->totalPCMFrameCount;
    }
    else if (starts_with(bytes, size, "OggS"))
    {
        d->kind = KIND_VORBIS;
        int error = 0;
        d->u.vorbis = stb_vorbis_open_memory(bytes, (int)(size > 0x7FFFFFFF ? 0x7FFFFFFF : size), &error, NULL);
        if (d->u.vorbis == NULL)
            goto fail;
        stb_vorbis_info vi = stb_vorbis_get_info(d->u.vorbis);
        d->channels = (uint32_t)vi.channels;
        info->channels = (uint32_t)vi.channels;
        info->sample_rate = vi.sample_rate;
        info->frames = stb_vorbis_stream_length_in_samples(d->u.vorbis);
    }
    else
    {
        // Anything else is tried as MP3 (with or without an ID3 tag): its decoder looks for the frame sync itself.
        d->kind = KIND_MP3;
        if (!drmp3_init_memory(&d->u.mp3, data, size, NULL))
            goto fail;
        d->channels = d->u.mp3.channels;
        info->channels = d->u.mp3.channels;
        info->sample_rate = d->u.mp3.sampleRate;
        info->frames = drmp3_get_pcm_frame_count(&d->u.mp3);
        drmp3_seek_to_pcm_frame(&d->u.mp3, 0);
    }
    if (info->channels == 0 || info->sample_rate == 0)
    {
        aa_close(d);
        return NULL;
    }
    return d;

fail:
    free(d);
    return NULL;
}

uint64_t aa_read(AaDecoder* d, float* out, uint64_t frames)
{
    if (d == NULL || out == NULL || frames == 0)
        return 0;
    switch (d->kind)
    {
    case KIND_WAV:
        return drwav_read_pcm_frames_f32(&d->u.wav, frames, out);
    case KIND_FLAC:
        return drflac_read_pcm_frames_f32(d->u.flac, frames, out);
    case KIND_MP3:
        return drmp3_read_pcm_frames_f32(&d->u.mp3, frames, out);
    case KIND_VORBIS:
    {
        uint64_t done = 0;
        while (done < frames)
        {
            uint64_t want = frames - done;
            if (want > 0x7FFFFFFF / d->channels)
                want = 0x7FFFFFFF / d->channels;
            int n = stb_vorbis_get_samples_float_interleaved(d->u.vorbis, (int)d->channels, out + done * d->channels, (int)(want * d->channels));
            if (n <= 0)
                break;
            done += (uint64_t)n;
        }
        return done;
    }
    }
    return 0;
}

int32_t aa_rewind(AaDecoder* d)
{
    if (d == NULL)
        return 0;
    switch (d->kind)
    {
    case KIND_WAV:
        return drwav_seek_to_pcm_frame(&d->u.wav, 0) == DRWAV_TRUE;
    case KIND_FLAC:
        return drflac_seek_to_pcm_frame(d->u.flac, 0) == DRFLAC_TRUE;
    case KIND_MP3:
        return drmp3_seek_to_pcm_frame(&d->u.mp3, 0) == DRMP3_TRUE;
    case KIND_VORBIS:
        return stb_vorbis_seek_start(d->u.vorbis) != 0;
    }
    return 0;
}

void aa_close(AaDecoder* d)
{
    if (d == NULL)
        return;
    switch (d->kind)
    {
    case KIND_WAV:
        drwav_uninit(&d->u.wav);
        break;
    case KIND_FLAC:
        drflac_close(d->u.flac);
        break;
    case KIND_MP3:
        drmp3_uninit(&d->u.mp3);
        break;
    case KIND_VORBIS:
        stb_vorbis_close(d->u.vorbis);
        break;
    }
    free(d);
}
