// A C API over Metal (which has only an Objective-C one), for the bindings generator. It is thin on purpose: one function per Metal call the engine's Metal backend
// makes, with the engine's own decisions (what to cache, when to wait) left to C#. Objects are opaque handles; every function that returns one hands over a reference that
// apogee_mtl_release gives back. Blocks and completion handlers never cross this API: waiting is done with command buffers (apogee_mtl_command_buffer_wait) and shared events.
// Failures that carry a message (a shader that does not compile) write it into a buffer the caller passes. macOS only (see Native/CMakeLists.txt); on other systems the library is not built.
#ifndef APOGEE_METAL_H
#define APOGEE_METAL_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifndef APOGEE_METAL_API
#define APOGEE_METAL_API __attribute__((visibility("default")))
#endif

typedef struct ApogeeMtlDevice ApogeeMtlDevice;
typedef struct ApogeeMtlQueue ApogeeMtlQueue;
typedef struct ApogeeMtlCommandBuffer ApogeeMtlCommandBuffer;
typedef struct ApogeeMtlBuffer ApogeeMtlBuffer;
typedef struct ApogeeMtlTexture ApogeeMtlTexture;
typedef struct ApogeeMtlSampler ApogeeMtlSampler;
typedef struct ApogeeMtlLibrary ApogeeMtlLibrary;
typedef struct ApogeeMtlRenderPipeline ApogeeMtlRenderPipeline;
typedef struct ApogeeMtlComputePipeline ApogeeMtlComputePipeline;
typedef struct ApogeeMtlDepthStencil ApogeeMtlDepthStencil;
typedef struct ApogeeMtlRenderEncoder ApogeeMtlRenderEncoder;
typedef struct ApogeeMtlComputeEncoder ApogeeMtlComputeEncoder;
typedef struct ApogeeMtlBlitEncoder ApogeeMtlBlitEncoder;
typedef struct ApogeeMtlEvent ApogeeMtlEvent;
typedef struct ApogeeMtlFence ApogeeMtlFence;
typedef struct ApogeeMtlResidencySet ApogeeMtlResidencySet;
typedef struct ApogeeMtlLayer ApogeeMtlLayer;
typedef struct ApogeeMtlDrawable ApogeeMtlDrawable;

/** Gives back the reference a creating function handed over. Null is allowed. */
APOGEE_METAL_API void apogee_mtl_release(void* handle);

// ---- Devices ----------------------------------------------------------------------------------------------------------------------

typedef struct ApogeeMtlDeviceInfo
{
    char name[128];
    uint64_t registry_id;
    uint64_t max_buffer_length;
    uint64_t recommended_working_set_size;
    int32_t is_low_power;
    int32_t is_removable;
    int32_t has_unified_memory;
    /** 0 tier 1, 1 tier 2 (the engine needs tier 2: argument buffers that hold arrays of textures). */
    int32_t argument_buffers_tier;
    /** The GPU family "Metal 3" (Apple silicon, and the Macs of 2017 and later that run macOS 13). */
    int32_t supports_metal3;
    /** An Apple-silicon GPU: ASTC textures, unified memory, tile shading. */
    int32_t supports_apple_family;
    int32_t supports_bc_textures;
    /** Timestamps in a counter sample buffer at the boundaries of passes. */
    int32_t supports_timestamp_counters;
    /** MTLResidencySet: macOS 15 and later. */
    int32_t supports_residency_sets;
    int32_t max_threads_per_threadgroup;
} ApogeeMtlDeviceInfo;

/** How many Metal GPUs the system has. */
APOGEE_METAL_API int32_t apogee_mtl_device_count(void);

/** The device at `index` (0 is the system's default), or null. */
APOGEE_METAL_API ApogeeMtlDevice* apogee_mtl_device_get(int32_t index);

APOGEE_METAL_API void apogee_mtl_device_info(const ApogeeMtlDevice* device, ApogeeMtlDeviceInfo* out_info);

/** The macOS version, such as 15.1.0, for logs and the baseline check. */
APOGEE_METAL_API void apogee_mtl_os_version(int32_t* major, int32_t* minor, int32_t* patch);

APOGEE_METAL_API ApogeeMtlQueue* apogee_mtl_queue_create(ApogeeMtlDevice* device);

// ---- Buffers ----------------------------------------------------------------------------------------------------------------------

#define APOGEE_MTL_STORAGE_SHARED 0
#define APOGEE_MTL_STORAGE_PRIVATE 1
#define APOGEE_MTL_STORAGE_MANAGED 2

/** `storage` is an APOGEE_MTL_STORAGE_*; `write_combined` is for memory the CPU only writes (uploads). */
APOGEE_METAL_API ApogeeMtlBuffer* apogee_mtl_buffer_create(ApogeeMtlDevice* device, uint64_t length, int32_t storage, int32_t write_combined);

/** The CPU address of a shared or managed buffer, null for a private one. */
APOGEE_METAL_API void* apogee_mtl_buffer_contents(const ApogeeMtlBuffer* buffer);

/** The GPU address a shader's pointer holds. */
APOGEE_METAL_API uint64_t apogee_mtl_buffer_gpu_address(const ApogeeMtlBuffer* buffer);

APOGEE_METAL_API uint64_t apogee_mtl_buffer_length(const ApogeeMtlBuffer* buffer);

/** Tells a managed buffer which bytes the CPU changed. */
APOGEE_METAL_API void apogee_mtl_buffer_did_modify_range(ApogeeMtlBuffer* buffer, uint64_t offset, uint64_t length);

APOGEE_METAL_API void apogee_mtl_buffer_set_label(ApogeeMtlBuffer* buffer, const char* label);

// ---- Textures and samplers -------------------------------------------------------------------------------------------------------

/** The engine's PixelFormat (the VkFormat number) as an MTLPixelFormat value; 0 for a format Metal does not have. */
APOGEE_METAL_API uint32_t apogee_mtl_pixel_format(int32_t engine_format);

#define APOGEE_MTL_TEXTURE_2D 0
#define APOGEE_MTL_TEXTURE_2D_ARRAY 1
#define APOGEE_MTL_TEXTURE_CUBE 2
#define APOGEE_MTL_TEXTURE_CUBE_ARRAY 3
#define APOGEE_MTL_TEXTURE_3D 4

#define APOGEE_MTL_USAGE_SHADER_READ 1
#define APOGEE_MTL_USAGE_SHADER_WRITE 2
#define APOGEE_MTL_USAGE_RENDER_TARGET 4
#define APOGEE_MTL_USAGE_PIXEL_FORMAT_VIEW 16

typedef struct ApogeeMtlTextureDesc
{
    int32_t type;
    /** An MTLPixelFormat value. */
    uint32_t pixel_format;
    uint32_t width;
    uint32_t height;
    uint32_t depth;
    uint32_t mip_levels;
    /** Array layers, per cube face group for the cube types (a cube is one layer here, six faces there). */
    uint32_t array_length;
    uint32_t sample_count;
    uint32_t usage;
} ApogeeMtlTextureDesc;

APOGEE_METAL_API ApogeeMtlTexture* apogee_mtl_texture_create(ApogeeMtlDevice* device, const ApogeeMtlTextureDesc* desc);

/** A view of levels and slices of a texture, as `type` and `pixel_format` (0 keeps the texture's). */
APOGEE_METAL_API ApogeeMtlTexture* apogee_mtl_texture_view(
    ApogeeMtlTexture* texture, uint32_t pixel_format, int32_t type, uint32_t base_level, uint32_t level_count, uint32_t base_slice, uint32_t slice_count);

/** What an argument buffer holds for this texture (an MTLResourceID). */
APOGEE_METAL_API uint64_t apogee_mtl_texture_resource_id(const ApogeeMtlTexture* texture);

APOGEE_METAL_API void apogee_mtl_texture_set_label(ApogeeMtlTexture* texture, const char* label);

#define APOGEE_MTL_FILTER_NEAREST 0
#define APOGEE_MTL_FILTER_LINEAR 1
#define APOGEE_MTL_ADDRESS_REPEAT 0
#define APOGEE_MTL_ADDRESS_MIRRORED_REPEAT 1
#define APOGEE_MTL_ADDRESS_CLAMP_TO_EDGE 2

typedef struct ApogeeMtlSamplerDesc
{
    int32_t filter;
    int32_t mip_filter;
    int32_t address_mode;
    int32_t max_anisotropy;
    /** Compare with "reference >= depth". */
    int32_t compare;
} ApogeeMtlSamplerDesc;

/** Always made for use in an argument buffer. */
APOGEE_METAL_API ApogeeMtlSampler* apogee_mtl_sampler_create(ApogeeMtlDevice* device, const ApogeeMtlSamplerDesc* desc);

APOGEE_METAL_API uint64_t apogee_mtl_sampler_resource_id(const ApogeeMtlSampler* sampler);

// ---- Shaders and pipelines ---------------------------------------------------------------------------------------------------------

/** Compiles Metal shading language. On failure returns null and writes the compiler's message (NUL-terminated, cut to fit) into `error`. */
APOGEE_METAL_API ApogeeMtlLibrary* apogee_mtl_library_create(ApogeeMtlDevice* device, const char* source, char* error, int32_t error_capacity);

#define APOGEE_MTL_BLEND_OPAQUE 0
#define APOGEE_MTL_BLEND_ALPHA 1
#define APOGEE_MTL_BLEND_PREMULTIPLIED 2
#define APOGEE_MTL_BLEND_ADDITIVE 3

typedef struct ApogeeMtlRenderPipelineDesc
{
    /** MTLPixelFormat values; up to 8 colour attachments. */
    uint32_t color_formats[8];
    int32_t color_count;
    uint32_t depth_format;
    uint32_t stencil_format;
    int32_t blend;
    int32_t sample_count;
    /** MTLPrimitiveTopologyClass: 1 point, 2 line, 3 triangle. */
    int32_t topology_class;
} ApogeeMtlRenderPipelineDesc;

/** `fragment_name` may be null (a depth-only pass). */
APOGEE_METAL_API ApogeeMtlRenderPipeline* apogee_mtl_render_pipeline_create(
    ApogeeMtlDevice* device, ApogeeMtlLibrary* vertex_library, const char* vertex_name, ApogeeMtlLibrary* fragment_library, const char* fragment_name,
    const ApogeeMtlRenderPipelineDesc* desc, char* error, int32_t error_capacity);

APOGEE_METAL_API ApogeeMtlComputePipeline* apogee_mtl_compute_pipeline_create(
    ApogeeMtlDevice* device, ApogeeMtlLibrary* library, const char* name, char* error, int32_t error_capacity);

/** `compare` is an MTLCompareFunction value. */
APOGEE_METAL_API ApogeeMtlDepthStencil* apogee_mtl_depth_stencil_create(ApogeeMtlDevice* device, int32_t compare, int32_t write_enabled);

// ---- Residency: what the GPU may touch through an address or an argument buffer ----------------------------------------------------

/** A residency set added to the queue: everything in it is resident for every command buffer. Null when the system has no residency sets (before macOS 15). */
APOGEE_METAL_API ApogeeMtlResidencySet* apogee_mtl_residency_set_create(ApogeeMtlDevice* device, ApogeeMtlQueue* queue);

APOGEE_METAL_API void apogee_mtl_residency_set_add_buffer(ApogeeMtlResidencySet* set, ApogeeMtlBuffer* buffer);

APOGEE_METAL_API void apogee_mtl_residency_set_add_texture(ApogeeMtlResidencySet* set, ApogeeMtlTexture* texture);

APOGEE_METAL_API void apogee_mtl_residency_set_remove_buffer(ApogeeMtlResidencySet* set, ApogeeMtlBuffer* buffer);

APOGEE_METAL_API void apogee_mtl_residency_set_remove_texture(ApogeeMtlResidencySet* set, ApogeeMtlTexture* texture);

/** Makes the additions and removals since the last commit take effect. */
APOGEE_METAL_API void apogee_mtl_residency_set_commit(ApogeeMtlResidencySet* set);

// ---- Command buffers and events -----------------------------------------------------------------------------------------------------

APOGEE_METAL_API ApogeeMtlCommandBuffer* apogee_mtl_command_buffer_create(ApogeeMtlQueue* queue);

APOGEE_METAL_API void apogee_mtl_command_buffer_commit(ApogeeMtlCommandBuffer* command_buffer);

/** Blocks until the GPU has finished the (committed) command buffer. */
APOGEE_METAL_API void apogee_mtl_command_buffer_wait(ApogeeMtlCommandBuffer* command_buffer);

/** 0 when it completed, otherwise the error's code (and `error` has its text). Only meaningful after the buffer finished. */
APOGEE_METAL_API int32_t apogee_mtl_command_buffer_error(const ApogeeMtlCommandBuffer* command_buffer, char* error, int32_t error_capacity);

APOGEE_METAL_API void apogee_mtl_command_buffer_encode_signal(ApogeeMtlCommandBuffer* command_buffer, ApogeeMtlEvent* event, uint64_t value);

APOGEE_METAL_API void apogee_mtl_command_buffer_present(ApogeeMtlCommandBuffer* command_buffer, ApogeeMtlDrawable* drawable);

APOGEE_METAL_API void apogee_mtl_command_buffer_push_debug_group(ApogeeMtlCommandBuffer* command_buffer, const char* name);

APOGEE_METAL_API void apogee_mtl_command_buffer_pop_debug_group(ApogeeMtlCommandBuffer* command_buffer);

/** A shared event: the frame loop's timeline (the value says how many frames the GPU has finished). */
APOGEE_METAL_API ApogeeMtlEvent* apogee_mtl_event_create(ApogeeMtlDevice* device);

APOGEE_METAL_API uint64_t apogee_mtl_event_signaled_value(const ApogeeMtlEvent* event);

/** Waits until the event reaches `value`; returns 0 when it did, 1 when `timeout_ms` passed first. */
APOGEE_METAL_API int32_t apogee_mtl_event_wait(ApogeeMtlEvent* event, uint64_t value, uint64_t timeout_ms);

/**
 * A fence orders encoders of one command buffer. Metal does not track what a shader reaches through an address or an argument buffer, so the backend chains its encoders: each waits for the
 * fence at its start (when an encoder came before it) and updates it at its end.
 */
APOGEE_METAL_API ApogeeMtlFence* apogee_mtl_fence_create(ApogeeMtlDevice* device);

// ---- Encoders -------------------------------------------------------------------------------------------------------------------------

#define APOGEE_MTL_USAGE_READ 1
#define APOGEE_MTL_USAGE_WRITE 2

APOGEE_METAL_API ApogeeMtlComputeEncoder* apogee_mtl_compute_encoder_begin(ApogeeMtlCommandBuffer* command_buffer);

APOGEE_METAL_API void apogee_mtl_compute_encoder_set_pipeline(ApogeeMtlComputeEncoder* encoder, ApogeeMtlComputePipeline* pipeline);

APOGEE_METAL_API void apogee_mtl_compute_encoder_set_bytes(ApogeeMtlComputeEncoder* encoder, const void* bytes, uint32_t length, uint32_t index);

APOGEE_METAL_API void apogee_mtl_compute_encoder_set_buffer(ApogeeMtlComputeEncoder* encoder, ApogeeMtlBuffer* buffer, uint64_t offset, uint32_t index);

APOGEE_METAL_API void apogee_mtl_compute_encoder_wait_fence(ApogeeMtlComputeEncoder* encoder, ApogeeMtlFence* fence);

APOGEE_METAL_API void apogee_mtl_compute_encoder_update_fence(ApogeeMtlComputeEncoder* encoder, ApogeeMtlFence* fence);

APOGEE_METAL_API void apogee_mtl_compute_encoder_dispatch(
    ApogeeMtlComputeEncoder* encoder, uint32_t groups_x, uint32_t groups_y, uint32_t groups_z, uint32_t threads_x, uint32_t threads_y, uint32_t threads_z);

/** Ends encoding and gives back the encoder's handle (it must not be used or released afterwards). */
APOGEE_METAL_API void apogee_mtl_compute_encoder_end(ApogeeMtlComputeEncoder* encoder);

APOGEE_METAL_API ApogeeMtlBlitEncoder* apogee_mtl_blit_encoder_begin(ApogeeMtlCommandBuffer* command_buffer);

APOGEE_METAL_API void apogee_mtl_blit_encoder_wait_fence(ApogeeMtlBlitEncoder* encoder, ApogeeMtlFence* fence);

APOGEE_METAL_API void apogee_mtl_blit_encoder_update_fence(ApogeeMtlBlitEncoder* encoder, ApogeeMtlFence* fence);

APOGEE_METAL_API void apogee_mtl_blit_encoder_copy_buffer(
    ApogeeMtlBlitEncoder* encoder, ApogeeMtlBuffer* source, uint64_t source_offset, ApogeeMtlBuffer* destination, uint64_t destination_offset, uint64_t size);

/** Copies a region of a buffer (rows of `bytes_per_row`, images of `bytes_per_image`) into a level and slice of a texture. */
APOGEE_METAL_API void apogee_mtl_blit_encoder_copy_buffer_to_texture(
    ApogeeMtlBlitEncoder* encoder, ApogeeMtlBuffer* source, uint64_t source_offset, uint32_t bytes_per_row, uint32_t bytes_per_image,
    ApogeeMtlTexture* texture, uint32_t level, uint32_t slice, uint32_t width, uint32_t height, uint32_t depth);

APOGEE_METAL_API void apogee_mtl_blit_encoder_copy_texture_to_buffer(
    ApogeeMtlBlitEncoder* encoder, ApogeeMtlTexture* texture, uint32_t level, uint32_t slice, uint32_t width, uint32_t height, uint32_t depth,
    ApogeeMtlBuffer* destination, uint64_t destination_offset, uint32_t bytes_per_row, uint32_t bytes_per_image);

/** Ends encoding and gives back the encoder's handle. */
APOGEE_METAL_API void apogee_mtl_blit_encoder_end(ApogeeMtlBlitEncoder* encoder);

typedef struct ApogeeMtlColorAttachment
{
    ApogeeMtlTexture* texture;
    /** 1 clear, 0 load. */
    int32_t clear;
    /** 1 store, 0 discard. */
    int32_t store;
    float clear_color[4];
} ApogeeMtlColorAttachment;

typedef struct ApogeeMtlDepthAttachment
{
    ApogeeMtlTexture* texture;
    int32_t clear;
    int32_t store;
    float clear_depth;
} ApogeeMtlDepthAttachment;

/** `depth` may be null. */
APOGEE_METAL_API ApogeeMtlRenderEncoder* apogee_mtl_render_encoder_begin(
    ApogeeMtlCommandBuffer* command_buffer, const ApogeeMtlColorAttachment* colors, int32_t color_count, const ApogeeMtlDepthAttachment* depth);

APOGEE_METAL_API void apogee_mtl_render_encoder_wait_fence(ApogeeMtlRenderEncoder* encoder, ApogeeMtlFence* fence);

APOGEE_METAL_API void apogee_mtl_render_encoder_update_fence(ApogeeMtlRenderEncoder* encoder, ApogeeMtlFence* fence);

APOGEE_METAL_API void apogee_mtl_render_encoder_set_pipeline(ApogeeMtlRenderEncoder* encoder, ApogeeMtlRenderPipeline* pipeline);

APOGEE_METAL_API void apogee_mtl_render_encoder_set_depth_stencil(ApogeeMtlRenderEncoder* encoder, ApogeeMtlDepthStencil* state);

/** `cull`: 0 none, 1 front, 2 back (MTLCullMode). `front_ccw`: 1 when counter-clockwise triangles are front faces. */
APOGEE_METAL_API void apogee_mtl_render_encoder_set_rasterizer(ApogeeMtlRenderEncoder* encoder, int32_t cull, int32_t front_ccw);

APOGEE_METAL_API void apogee_mtl_render_encoder_set_viewport(ApogeeMtlRenderEncoder* encoder, double x, double y, double width, double height, double near_depth, double far_depth);

APOGEE_METAL_API void apogee_mtl_render_encoder_set_scissor(ApogeeMtlRenderEncoder* encoder, uint32_t x, uint32_t y, uint32_t width, uint32_t height);

/** Small constants for both stages at once (the engine's push constants). */
APOGEE_METAL_API void apogee_mtl_render_encoder_set_bytes(ApogeeMtlRenderEncoder* encoder, const void* bytes, uint32_t length, uint32_t index);

APOGEE_METAL_API void apogee_mtl_render_encoder_set_fragment_buffer(ApogeeMtlRenderEncoder* encoder, ApogeeMtlBuffer* buffer, uint64_t offset, uint32_t index);

/** `primitive`: MTLPrimitiveType (0 point, 1 line, 2 line strip, 3 triangle, 4 triangle strip). */
APOGEE_METAL_API void apogee_mtl_render_encoder_draw(
    ApogeeMtlRenderEncoder* encoder, int32_t primitive, uint32_t vertex_start, uint32_t vertex_count, uint32_t instance_count, uint32_t base_instance);

/** `index_type`: 0 uint16, 1 uint32 (MTLIndexType). */
APOGEE_METAL_API void apogee_mtl_render_encoder_draw_indexed(
    ApogeeMtlRenderEncoder* encoder, int32_t primitive, uint32_t index_count, int32_t index_type, ApogeeMtlBuffer* index_buffer, uint64_t index_offset,
    uint32_t instance_count, int32_t base_vertex, uint32_t base_instance);

/** Ends encoding and gives back the encoder's handle. */
APOGEE_METAL_API void apogee_mtl_render_encoder_end(ApogeeMtlRenderEncoder* encoder);

// ---- The window: a CAMetalLayer ---------------------------------------------------------------------------------------------------------

/** Makes (or finds) the layer of an SDL Metal view (`SDL_Metal_GetLayer`'s pointer) and sets it up for `device`: BGRA8, drawable size, vsync. */
APOGEE_METAL_API ApogeeMtlLayer* apogee_mtl_layer_attach(void* sdl_layer, ApogeeMtlDevice* device, uint32_t width, uint32_t height, int32_t vsync);

APOGEE_METAL_API void apogee_mtl_layer_resize(ApogeeMtlLayer* layer, uint32_t width, uint32_t height);

/** The next drawable, or null when none is available. Its texture is borrowed: it is valid while the drawable is. */
APOGEE_METAL_API ApogeeMtlDrawable* apogee_mtl_layer_next_drawable(ApogeeMtlLayer* layer);

/** The drawable's texture as a retained texture handle (release it when the frame is done). */
APOGEE_METAL_API ApogeeMtlTexture* apogee_mtl_drawable_texture(const ApogeeMtlDrawable* drawable);

#ifdef __cplusplus
}
#endif

#endif
