// A C API over Slang's COM interface (slang.h is C++ only), for the bindings generator. Compiles Slang source to SPIR-V
// (Vulkan 1.3, SPIR-V 1.6) or Metal shading language text, one target per session, with row-major matrices and entry points that
// keep their names, and reports diagnostics, reflection and the files a compile read. All functions may be called from any thread; a session is used by one thread at a time.
#ifndef APOGEE_SLANG_H
#define APOGEE_SLANG_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifndef APOGEE_SLANG_API
#define APOGEE_SLANG_API
#endif

typedef struct ApogeeSlangSession ApogeeSlangSession;
typedef struct ApogeeSlangResult ApogeeSlangResult;

typedef struct ApogeeSlangMacro
{
    const char* name;
    const char* value;
} ApogeeSlangMacro;

typedef struct ApogeeSlangSessionDesc
{
    /** Directories searched for `import` and `#include`. */
    const char* const* search_paths;
    int32_t search_path_count;
    const ApogeeSlangMacro* macros;
    int32_t macro_count;
    /** 0 none, 1 default, 2 high, 3 maximal. */
    int32_t optimization;
    /** 0 none, 1 minimal, 2 standard, 3 maximal. */
    int32_t debug_info;
    /** What the session produces: APOGEE_SLANG_TARGET_SPIRV (0, the default) or APOGEE_SLANG_TARGET_METAL (1, MSL source text). */
    int32_t target;
} ApogeeSlangSessionDesc;

#define APOGEE_SLANG_TARGET_SPIRV 0
#define APOGEE_SLANG_TARGET_METAL 1

/** The Slang version this was built against, such as "2026.19". */
APOGEE_SLANG_API const char* apogee_slang_version(void);

/** Creates a session for `desc->target`. Returns a SlangResult: 0 or a positive value is success, negative is failure. */
APOGEE_SLANG_API int32_t apogee_slang_session_create(const ApogeeSlangSessionDesc* desc, ApogeeSlangSession** out_session);

APOGEE_SLANG_API void apogee_slang_session_destroy(ApogeeSlangSession* session);

/**
 * Compiles a module and links its entry points into SPIR-V, one blob per entry point.
 * `source` is the module's text and `path` its name for diagnostics; when `source` is null the module is loaded from the
 * search paths as `<module_name>.slang`. With `entry_point_count` 0 every entry point the module defines is compiled.
 * Always sets `out_result` (even on failure, to carry the diagnostics) and returns its status.
 */
APOGEE_SLANG_API int32_t apogee_slang_compile(
    ApogeeSlangSession* session, const char* module_name, const char* path, const char* source,
    const char* const* entry_points, int32_t entry_point_count, ApogeeSlangResult** out_result);

/** 0 or positive on success, negative on failure. */
APOGEE_SLANG_API int32_t apogee_slang_result_status(const ApogeeSlangResult* result);

/** Warnings and errors, one string, empty when there are none. Valid until the result is destroyed. */
APOGEE_SLANG_API const char* apogee_slang_result_diagnostics(const ApogeeSlangResult* result);

APOGEE_SLANG_API int32_t apogee_slang_result_entry_point_count(const ApogeeSlangResult* result);

/** The entry point's name as written in the shader. */
APOGEE_SLANG_API const char* apogee_slang_result_entry_point_name(const ApogeeSlangResult* result, int32_t index);

/** The entry point's code for the session's target: SPIR-V words (a multiple of 4 bytes), or Metal shading language text. */
APOGEE_SLANG_API const void* apogee_slang_result_code(const ApogeeSlangResult* result, int32_t index, size_t* out_size);

/** The program's reflection (parameters, bindings, entry points, thread group sizes) as Slang's JSON. */
APOGEE_SLANG_API const char* apogee_slang_result_reflection_json(const ApogeeSlangResult* result);

/** The files the compile read (the module and everything it included or imported), as paths. */
APOGEE_SLANG_API int32_t apogee_slang_result_dependency_count(const ApogeeSlangResult* result);

APOGEE_SLANG_API const char* apogee_slang_result_dependency(const ApogeeSlangResult* result, int32_t index);

APOGEE_SLANG_API void apogee_slang_result_destroy(ApogeeSlangResult* result);

#ifdef __cplusplus
}
#endif

#endif
