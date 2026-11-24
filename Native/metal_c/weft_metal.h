// A C API over Metal (which has only an Objective-C one), for the bindings generator. It is thin on purpose: one function per Metal call the engine's Metal backend
// makes, with the engine's own decisions (what to cache, when to wait) left to C#. Objects are opaque handles; every function that returns one hands over a reference that
// weft_mtl_release gives back. Blocks and completion handlers never cross this API: waiting is done with command buffers (weft_mtl_command_buffer_wait) and shared events.
// Failures that carry a message (a shader that does not compile) write it into a buffer the caller passes. macOS only (see Native/CMakeLists.txt); on other systems the library is not built.
#ifndef WEFT_METAL_H
#define WEFT_METAL_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifndef WEFT_METAL_API
#define WEFT_METAL_API __attribute__((visibility("default")))
#endif

typedef struct WeftMtlDevice WeftMtlDevice;
typedef struct WeftMtlQueue WeftMtlQueue;
typedef struct WeftMtlCommandBuffer WeftMtlCommandBuffer;
typedef struct WeftMtlBuffer WeftMtlBuffer;
typedef struct WeftMtlTexture WeftMtlTexture;
typedef struct WeftMtlSampler WeftMtlSampler;
typedef struct WeftMtlLibrary WeftMtlLibrary;
typedef struct WeftMtlRenderPipeline WeftMtlRenderPipeline;
typedef struct WeftMtlComputePipeline WeftMtlComputePipeline;
typedef struct WeftMtlDepthStencil WeftMtlDepthStencil;
typedef struct WeftMtlRenderEncoder WeftMtlRenderEncoder;
typedef struct WeftMtlComputeEncoder WeftMtlComputeEncoder;
typedef struct WeftMtlBlitEncoder WeftMtlBlitEncoder;
typedef struct WeftMtlEvent WeftMtlEvent;
typedef struct WeftMtlFence WeftMtlFence;
typedef struct WeftMtlResidencySet WeftMtlResidencySet;
typedef struct WeftMtlLayer WeftMtlLayer;
typedef struct WeftMtlDrawable WeftMtlDrawable;

/** Gives back the reference a creating function handed over. Null is allowed. */
WEFT_METAL_API void weft_mtl_release(void* handle);

// ---- Devices ----------------------------------------------------------------------------------------------------------------------

typedef struct WeftMtlDeviceInfo
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
} WeftMtlDeviceInfo;

/** How many Metal GPUs the system has. */
WEFT_METAL_API int32_t weft_mtl_device_count(void);

/** The device at `index` (0 is the system's default), or null. */
WEFT_METAL_API WeftMtlDevice* weft_mtl_device_get(int32_t index);

WEFT_METAL_API void weft_mtl_device_info(const WeftMtlDevice* device, WeftMtlDeviceInfo* out_info);

/** The macOS version, such as 15.1.0, for logs and the baseline check. */
WEFT_METAL_API void weft_mtl_os_version(int32_t* major, int32_t* minor, int32_t* patch);

WEFT_METAL_API WeftMtlQueue* weft_mtl_queue_create(WeftMtlDevice* device);

// ---- Buffers ----------------------------------------------------------------------------------------------------------------------

#define WEFT_MTL_STORAGE_SHARED 0
#define WEFT_MTL_STORAGE_PRIVATE 1
#define WEFT_MTL_STORAGE_MANAGED 2

/** `storage` is a WEFT_MTL_STORAGE_*; `write_combined` is for memory the CPU only writes (uploads). */
WEFT_METAL_API WeftMtlBuffer* weft_mtl_buffer_create(WeftMtlDevice* device, uint64_t length, int32_t storage, int32_t write_combined);

/** The CPU address of a shared or managed buffer, null for a private one. */
WEFT_METAL_API void* weft_mtl_buffer_contents(const WeftMtlBuffer* buffer);

/** The GPU address a shader's pointer holds. */
WEFT_METAL_API uint64_t weft_mtl_buffer_gpu_address(const WeftMtlBuffer* buffer);

WEFT_METAL_API uint64_t weft_mtl_buffer_length(const WeftMtlBuffer* buffer);

/** Tells a managed buffer which bytes the CPU changed. */
WEFT_METAL_API void weft_mtl_buffer_did_modify_range(WeftMtlBuffer* buffer, uint64_t offset, uint64_t length);

WEFT_METAL_API void weft_mtl_buffer_set_label(WeftMtlBuffer* buffer, const char* label);

// ---- Textures and samplers -------------------------------------------------------------------------------------------------------

/** The engine's PixelFormat (the VkFormat number) as an MTLPixelFormat value; 0 for a format Metal does not have. */
WEFT_METAL_API uint32_t weft_mtl_pixel_format(int32_t engine_format);

#define WEFT_MTL_TEXTURE_2D 0
#define WEFT_MTL_TEXTURE_2D_ARRAY 1
#define WEFT_MTL_TEXTURE_CUBE 2
#define WEFT_MTL_TEXTURE_CUBE_ARRAY 3
#define WEFT_MTL_TEXTURE_3D 4

#define WEFT_MTL_USAGE_SHADER_READ 1
#define WEFT_MTL_USAGE_SHADER_WRITE 2
#define WEFT_MTL_USAGE_RENDER_TARGET 4
#define WEFT_MTL_USAGE_PIXEL_FORMAT_VIEW 16

typedef struct WeftMtlTextureDesc
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
} WeftMtlTextureDesc;

WEFT_METAL_API WeftMtlTexture* weft_mtl_texture_create(WeftMtlDevice* device, const WeftMtlTextureDesc* desc);

/** A view of levels and slices of a texture, as `type` and `pixel_format` (0 keeps the texture's). */
WEFT_METAL_API WeftMtlTexture* weft_mtl_texture_view(
    WeftMtlTexture* texture, uint32_t pixel_format, int32_t type, uint32_t base_level, uint32_t level_count, uint32_t base_slice, uint32_t slice_count);

/** What an argument buffer holds for this texture (an MTLResourceID). */
WEFT_METAL_API uint64_t weft_mtl_texture_resource_id(const WeftMtlTexture* texture);

WEFT_METAL_API void weft_mtl_texture_set_label(WeftMtlTexture* texture, const char* label);

#define WEFT_MTL_FILTER_NEAREST 0
#define WEFT_MTL_FILTER_LINEAR 1
#define WEFT_MTL_ADDRESS_REPEAT 0
#define WEFT_MTL_ADDRESS_MIRRORED_REPEAT 1
#define WEFT_MTL_ADDRESS_CLAMP_TO_EDGE 2

typedef struct WeftMtlSamplerDesc
{
    int32_t filter;
    int32_t mip_filter;
    int32_t address_mode;
    int32_t max_anisotropy;
    /** Compare with "reference >= depth". */
    int32_t compare;
} WeftMtlSamplerDesc;

/** Always made for use in an argument buffer. */
WEFT_METAL_API WeftMtlSampler* weft_mtl_sampler_create(WeftMtlDevice* device, const WeftMtlSamplerDesc* desc);

WEFT_METAL_API uint64_t weft_mtl_sampler_resource_id(const WeftMtlSampler* sampler);

// ---- Shaders and pipelines ---------------------------------------------------------------------------------------------------------

/** Compiles Metal shading language. On failure returns null and writes the compiler's message (NUL-terminated, cut to fit) into `error`. */
WEFT_METAL_API WeftMtlLibrary* weft_mtl_library_create(WeftMtlDevice* device, const char* source, char* error, int32_t error_capacity);

#define WEFT_MTL_BLEND_OPAQUE 0
#define WEFT_MTL_BLEND_ALPHA 1
#define WEFT_MTL_BLEND_PREMULTIPLIED 2
#define WEFT_MTL_BLEND_ADDITIVE 3

typedef struct WeftMtlRenderPipelineDesc
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
} WeftMtlRenderPipelineDesc;

/** `fragment_name` may be null (a depth-only pass). */
WEFT_METAL_API WeftMtlRenderPipeline* weft_mtl_render_pipeline_create(
    WeftMtlDevice* device, WeftMtlLibrary* vertex_library, const char* vertex_name, WeftMtlLibrary* fragment_library, const char* fragment_name,
    const WeftMtlRenderPipelineDesc* desc, char* error, int32_t error_capacity);

WEFT_METAL_API WeftMtlComputePipeline* weft_mtl_compute_pipeline_create(
    WeftMtlDevice* device, WeftMtlLibrary* library, const char* name, char* error, int32_t error_capacity);

/** `compare` is an MTLCompareFunction value. */
WEFT_METAL_API WeftMtlDepthStencil* weft_mtl_depth_stencil_create(WeftMtlDevice* device, int32_t compare, int32_t write_enabled);

// ---- Residency: what the GPU may touch through an address or an argument buffer ----------------------------------------------------

/** A residency set added to the queue: everything in it is resident for every command buffer. Null when the system has no residency sets (before macOS 15). */
WEFT_METAL_API WeftMtlResidencySet* weft_mtl_residency_set_create(WeftMtlDevice* device, WeftMtlQueue* queue);

WEFT_METAL_API void weft_mtl_residency_set_add_buffer(WeftMtlResidencySet* set, WeftMtlBuffer* buffer);

WEFT_METAL_API void weft_mtl_residency_set_add_texture(WeftMtlResidencySet* set, WeftMtlTexture* texture);

WEFT_METAL_API void weft_mtl_residency_set_remove_buffer(WeftMtlResidencySet* set, WeftMtlBuffer* buffer);

WEFT_METAL_API void weft_mtl_residency_set_remove_texture(WeftMtlResidencySet* set, WeftMtlTexture* texture);

/** Makes the additions and removals since the last commit take effect. */
WEFT_METAL_API void weft_mtl_residency_set_commit(WeftMtlResidencySet* set);

// ---- Command buffers and events -----------------------------------------------------------------------------------------------------

WEFT_METAL_API WeftMtlCommandBuffer* weft_mtl_command_buffer_create(WeftMtlQueue* queue);

WEFT_METAL_API void weft_mtl_command_buffer_commit(WeftMtlCommandBuffer* command_buffer);

/** Blocks until the GPU has finished the (committed) command buffer. */
WEFT_METAL_API void weft_mtl_command_buffer_wait(WeftMtlCommandBuffer* command_buffer);

/** 0 when it completed, otherwise the error's code (and `error` has its text). Only meaningful after the buffer finished. */
WEFT_METAL_API int32_t weft_mtl_command_buffer_error(const WeftMtlCommandBuffer* command_buffer, char* error, int32_t error_capacity);

WEFT_METAL_API void weft_mtl_command_buffer_encode_signal(WeftMtlCommandBuffer* command_buffer, WeftMtlEvent* event, uint64_t value);

WEFT_METAL_API void weft_mtl_command_buffer_present(WeftMtlCommandBuffer* command_buffer, WeftMtlDrawable* drawable);

WEFT_METAL_API void weft_mtl_command_buffer_push_debug_group(WeftMtlCommandBuffer* command_buffer, const char* name);

WEFT_METAL_API void weft_mtl_command_buffer_pop_debug_group(WeftMtlCommandBuffer* command_buffer);

/** A shared event: the frame loop's timeline (the value says how many frames the GPU has finished). */
WEFT_METAL_API WeftMtlEvent* weft_mtl_event_create(WeftMtlDevice* device);

WEFT_METAL_API uint64_t weft_mtl_event_signaled_value(const WeftMtlEvent* event);

/** Waits until the event reaches `value`; returns 0 when it did, 1 when `timeout_ms` passed first. */
WEFT_METAL_API int32_t weft_mtl_event_wait(WeftMtlEvent* event, uint64_t value, uint64_t timeout_ms);

/**
 * A fence orders encoders of one command buffer. Metal does not track what a shader reaches through an address or an argument buffer, so the backend chains its encoders: each waits for the
 * fence at its start (when an encoder came before it) and updates it at its end.
 */
WEFT_METAL_API WeftMtlFence* weft_mtl_fence_create(WeftMtlDevice* device);

// ---- Encoders -------------------------------------------------------------------------------------------------------------------------

#define WEFT_MTL_USAGE_READ 1
#define WEFT_MTL_USAGE_WRITE 2

WEFT_METAL_API WeftMtlComputeEncoder* weft_mtl_compute_encoder_begin(WeftMtlCommandBuffer* command_buffer);

WEFT_METAL_API void weft_mtl_compute_encoder_set_pipeline(WeftMtlComputeEncoder* encoder, WeftMtlComputePipeline* pipeline);

WEFT_METAL_API void weft_mtl_compute_encoder_set_bytes(WeftMtlComputeEncoder* encoder, const void* bytes, uint32_t length, uint32_t index);

WEFT_METAL_API void weft_mtl_compute_encoder_set_buffer(WeftMtlComputeEncoder* encoder, WeftMtlBuffer* buffer, uint64_t offset, uint32_t index);

/** Runs the groups the buffer says (three uint32 at `offset`: the group counts, the layout of Vulkan's VkDispatchIndirectCommand too). */
WEFT_METAL_API void weft_mtl_compute_encoder_dispatch_indirect(
    WeftMtlComputeEncoder* encoder, WeftMtlBuffer* arguments, uint64_t offset, uint32_t threads_x, uint32_t threads_y, uint32_t threads_z);

WEFT_METAL_API void weft_mtl_compute_encoder_wait_fence(WeftMtlComputeEncoder* encoder, WeftMtlFence* fence);

WEFT_METAL_API void weft_mtl_compute_encoder_update_fence(WeftMtlComputeEncoder* encoder, WeftMtlFence* fence);

WEFT_METAL_API void weft_mtl_compute_encoder_dispatch(
    WeftMtlComputeEncoder* encoder, uint32_t groups_x, uint32_t groups_y, uint32_t groups_z, uint32_t threads_x, uint32_t threads_y, uint32_t threads_z);

/** Ends encoding and gives back the encoder's handle (it must not be used or released afterwards). */
WEFT_METAL_API void weft_mtl_compute_encoder_end(WeftMtlComputeEncoder* encoder);

WEFT_METAL_API WeftMtlBlitEncoder* weft_mtl_blit_encoder_begin(WeftMtlCommandBuffer* command_buffer);

WEFT_METAL_API void weft_mtl_blit_encoder_wait_fence(WeftMtlBlitEncoder* encoder, WeftMtlFence* fence);

WEFT_METAL_API void weft_mtl_blit_encoder_update_fence(WeftMtlBlitEncoder* encoder, WeftMtlFence* fence);

WEFT_METAL_API void weft_mtl_blit_encoder_copy_buffer(
    WeftMtlBlitEncoder* encoder, WeftMtlBuffer* source, uint64_t source_offset, WeftMtlBuffer* destination, uint64_t destination_offset, uint64_t size);

/** Copies a region of a buffer (rows of `bytes_per_row`, images of `bytes_per_image`) into a level and slice of a texture. */
WEFT_METAL_API void weft_mtl_blit_encoder_copy_buffer_to_texture(
    WeftMtlBlitEncoder* encoder, WeftMtlBuffer* source, uint64_t source_offset, uint32_t bytes_per_row, uint32_t bytes_per_image,
    WeftMtlTexture* texture, uint32_t level, uint32_t slice, uint32_t width, uint32_t height, uint32_t depth);

WEFT_METAL_API void weft_mtl_blit_encoder_copy_texture_to_buffer(
    WeftMtlBlitEncoder* encoder, WeftMtlTexture* texture, uint32_t level, uint32_t slice, uint32_t width, uint32_t height, uint32_t depth,
    WeftMtlBuffer* destination, uint64_t destination_offset, uint32_t bytes_per_row, uint32_t bytes_per_image);

/** Sets `size` bytes from `offset` to the byte `value` (Metal fills bytes, not words). */
WEFT_METAL_API void weft_mtl_blit_encoder_fill_buffer(WeftMtlBlitEncoder* encoder, WeftMtlBuffer* buffer, uint64_t offset, uint64_t size, int32_t value);

/** Ends encoding and gives back the encoder's handle. */
WEFT_METAL_API void weft_mtl_blit_encoder_end(WeftMtlBlitEncoder* encoder);

typedef struct WeftMtlColorAttachment
{
    WeftMtlTexture* texture;
    /** 1 clear, 0 load. */
    int32_t clear;
    /** 1 store, 0 discard. */
    int32_t store;
    float clear_color[4];
} WeftMtlColorAttachment;

typedef struct WeftMtlDepthAttachment
{
    WeftMtlTexture* texture;
    int32_t clear;
    int32_t store;
    float clear_depth;
} WeftMtlDepthAttachment;

/** `depth` may be null. */
WEFT_METAL_API WeftMtlRenderEncoder* weft_mtl_render_encoder_begin(
    WeftMtlCommandBuffer* command_buffer, const WeftMtlColorAttachment* colors, int32_t color_count, const WeftMtlDepthAttachment* depth);

WEFT_METAL_API void weft_mtl_render_encoder_wait_fence(WeftMtlRenderEncoder* encoder, WeftMtlFence* fence);

WEFT_METAL_API void weft_mtl_render_encoder_update_fence(WeftMtlRenderEncoder* encoder, WeftMtlFence* fence);

WEFT_METAL_API void weft_mtl_render_encoder_set_pipeline(WeftMtlRenderEncoder* encoder, WeftMtlRenderPipeline* pipeline);

WEFT_METAL_API void weft_mtl_render_encoder_set_depth_stencil(WeftMtlRenderEncoder* encoder, WeftMtlDepthStencil* state);

/** `cull`: 0 none, 1 front, 2 back (MTLCullMode). `front_ccw`: 1 when counter-clockwise triangles are front faces. */
WEFT_METAL_API void weft_mtl_render_encoder_set_rasterizer(WeftMtlRenderEncoder* encoder, int32_t cull, int32_t front_ccw);

WEFT_METAL_API void weft_mtl_render_encoder_set_viewport(WeftMtlRenderEncoder* encoder, double x, double y, double width, double height, double near_depth, double far_depth);

WEFT_METAL_API void weft_mtl_render_encoder_set_scissor(WeftMtlRenderEncoder* encoder, uint32_t x, uint32_t y, uint32_t width, uint32_t height);

/** Small constants for both stages at once (the engine's push constants). */
WEFT_METAL_API void weft_mtl_render_encoder_set_bytes(WeftMtlRenderEncoder* encoder, const void* bytes, uint32_t length, uint32_t index);

WEFT_METAL_API void weft_mtl_render_encoder_set_fragment_buffer(WeftMtlRenderEncoder* encoder, WeftMtlBuffer* buffer, uint64_t offset, uint32_t index);

/** `primitive`: MTLPrimitiveType (0 point, 1 line, 2 line strip, 3 triangle, 4 triangle strip). */
WEFT_METAL_API void weft_mtl_render_encoder_draw(
    WeftMtlRenderEncoder* encoder, int32_t primitive, uint32_t vertex_start, uint32_t vertex_count, uint32_t instance_count, uint32_t base_instance);

/** `index_type`: 0 uint16, 1 uint32 (MTLIndexType). */
WEFT_METAL_API void weft_mtl_render_encoder_draw_indexed(
    WeftMtlRenderEncoder* encoder, int32_t primitive, uint32_t index_count, int32_t index_type, WeftMtlBuffer* index_buffer, uint64_t index_offset,
    uint32_t instance_count, int32_t base_vertex, uint32_t base_instance);

/**
 * One draw whose arguments are in a buffer, five 32-bit words at `indirect_offset`: index count, instance count, first index, base vertex, base instance (the layout of Vulkan's
 * VkDrawIndexedIndirectCommand too).
 */
WEFT_METAL_API void weft_mtl_render_encoder_draw_indexed_indirect(
    WeftMtlRenderEncoder* encoder, int32_t primitive, int32_t index_type, WeftMtlBuffer* index_buffer, uint64_t index_offset, WeftMtlBuffer* indirect_buffer, uint64_t indirect_offset);

/** Ends encoding and gives back the encoder's handle. */
WEFT_METAL_API void weft_mtl_render_encoder_end(WeftMtlRenderEncoder* encoder);

// ---- The window: a CAMetalLayer ---------------------------------------------------------------------------------------------------------

/** Makes (or finds) the layer of an SDL Metal view (`SDL_Metal_GetLayer`'s pointer) and sets it up for `device`: BGRA8, drawable size, vsync. */
WEFT_METAL_API WeftMtlLayer* weft_mtl_layer_attach(void* sdl_layer, WeftMtlDevice* device, uint32_t width, uint32_t height, int32_t vsync);

WEFT_METAL_API void weft_mtl_layer_resize(WeftMtlLayer* layer, uint32_t width, uint32_t height);

/** The next drawable, or null when none is available. Its texture is borrowed: it is valid while the drawable is. */
WEFT_METAL_API WeftMtlDrawable* weft_mtl_layer_next_drawable(WeftMtlLayer* layer);

/** The drawable's texture as a retained texture handle (release it when the frame is done). */
WEFT_METAL_API WeftMtlTexture* weft_mtl_drawable_texture(const WeftMtlDrawable* drawable);

#ifdef __cplusplus
}
#endif

#endif
