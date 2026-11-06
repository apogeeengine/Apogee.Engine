/*
 * Our C API over the audio decoders (ThirdParty/dr_libs: WAV, FLAC and MP3; ThirdParty/stb/stb_vorbis.c: Ogg Vorbis). A decoder reads a whole file that is already in memory
 * (files come through the engine's file system) and produces 32-bit float samples, interleaved, at the file's own channel count and sample rate. The format is told from the first
 * bytes, not from a name.
 *
 * The memory passed to apogee_audio_open must stay valid until the decoder is closed. Errors: functions that can fail return null or 0; none throws.
 * A decoder is used from one thread at a time.
 */
#ifndef APOGEE_AUDIO_H
#define APOGEE_AUDIO_H

#include <stddef.h>
#include <stdint.h>

#ifndef APOGEE_AUDIO_API
#if defined(_WIN32)
#define APOGEE_AUDIO_API __declspec(dllexport)
#else
#define APOGEE_AUDIO_API __attribute__((visibility("default")))
#endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct AaDecoder AaDecoder;

typedef struct AaInfo
{
    uint32_t channels;
    uint32_t sample_rate;
    /** Length in frames (a frame is one sample per channel); 0 when the file does not say. */
    uint64_t frames;
} AaInfo;

/** Opens a decoder over `size` bytes of a WAV, FLAC, MP3 or Ogg Vorbis file and fills `info`; null if the format is unknown or the data is bad. */
APOGEE_AUDIO_API AaDecoder* aa_open(const void* data, size_t size, AaInfo* info);

/** Decodes up to `frames` frames into `out` (frames x channels floats); returns how many it wrote, 0 at the end. */
APOGEE_AUDIO_API uint64_t aa_read(AaDecoder* decoder, float* out, uint64_t frames);

/** Goes back to the first frame; false if it could not. */
APOGEE_AUDIO_API int32_t aa_rewind(AaDecoder* decoder);

APOGEE_AUDIO_API void aa_close(AaDecoder* decoder);

#ifdef __cplusplus
}
#endif

#endif
