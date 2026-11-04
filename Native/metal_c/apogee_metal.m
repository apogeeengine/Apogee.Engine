// The C API over Metal, see apogee_metal.h. Compiled with ARC. Every exported function runs in its own autorelease pool (the callers are .NET threads, which have none) and hands
// objects out with CFBridgingRetain, so a handle holds one reference until apogee_mtl_release (or an encoder's end, which releases the encoder).
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>
#include <float.h>
#include <string.h>

#include "apogee_metal.h"

#define HANDLE(Type, object) ((Type*)(void*)CFBridgingRetain(object))
#define GET(MtlType, handle) ((__bridge MtlType)((void*)(handle)))

static void CopyError(NSError* error, char* out, int32_t capacity)
{
    if (out == NULL || capacity <= 0)
    {
        return;
    }

    NSString* text = error != nil ? error.localizedDescription : @"unknown error";
    [text getCString:out maxLength:(NSUInteger)capacity encoding:NSUTF8StringEncoding];
    out[capacity - 1] = 0;
}

static NSArray<id<MTLDevice>>* AllDevices(void)
{
    // The system's default first, then the others, so that index 0 is what a program that asks for no particular GPU gets.
    NSMutableArray<id<MTLDevice>>* devices = [NSMutableArray array];
    id<MTLDevice> primary = MTLCreateSystemDefaultDevice();
    if (primary != nil)
    {
        [devices addObject:primary];
    }

    for (id<MTLDevice> device in MTLCopyAllDevices())
    {
        if (primary == nil || device.registryID != primary.registryID)
        {
            [devices addObject:device];
        }
    }

    return devices;
}

void apogee_mtl_release(void* handle)
{
    if (handle != NULL)
    {
        CFRelease(handle);
    }
}

// ---- Devices ----------------------------------------------------------------------------------------------------------------------

int32_t apogee_mtl_device_count(void)
{
    @autoreleasepool
    {
        return (int32_t)AllDevices().count;
    }
}

ApogeeMtlDevice* apogee_mtl_device_get(int32_t index)
{
    @autoreleasepool
    {
        NSArray<id<MTLDevice>>* devices = AllDevices();
        return index >= 0 && (NSUInteger)index < devices.count ? HANDLE(ApogeeMtlDevice, devices[(NSUInteger)index]) : NULL;
    }
}

void apogee_mtl_device_info(const ApogeeMtlDevice* handle, ApogeeMtlDeviceInfo* out)
{
    @autoreleasepool
    {
        id<MTLDevice> device = GET(id<MTLDevice>, handle);
        memset(out, 0, sizeof(*out));
        [device.name getCString:out->name maxLength:sizeof(out->name) encoding:NSUTF8StringEncoding];
        out->registry_id = device.registryID;
        out->max_buffer_length = device.maxBufferLength;
        out->recommended_working_set_size = device.recommendedMaxWorkingSetSize;
        out->is_low_power = device.lowPower ? 1 : 0;
        out->is_removable = device.removable ? 1 : 0;
        out->has_unified_memory = device.hasUnifiedMemory ? 1 : 0;
        out->argument_buffers_tier = device.argumentBuffersSupport == MTLArgumentBuffersTier2 ? 1 : 0;
        out->supports_metal3 = [device supportsFamily:MTLGPUFamilyMetal3] ? 1 : 0;
        out->supports_apple_family = [device supportsFamily:MTLGPUFamilyApple7] ? 1 : 0;
        out->supports_bc_textures = device.supportsBCTextureCompression ? 1 : 0;
        out->supports_timestamp_counters = [device supportsCounterSampling:MTLCounterSamplingPointAtStageBoundary] ? 1 : 0;
        out->supports_residency_sets = 0;
        if (@available(macOS 15.0, *))
        {
            out->supports_residency_sets = 1;
        }

        out->max_threads_per_threadgroup = (int32_t)device.maxThreadsPerThreadgroup.width;
    }
}

void apogee_mtl_os_version(int32_t* major, int32_t* minor, int32_t* patch)
{
    @autoreleasepool
    {
        NSOperatingSystemVersion version = [NSProcessInfo processInfo].operatingSystemVersion;
        *major = (int32_t)version.majorVersion;
        *minor = (int32_t)version.minorVersion;
        *patch = (int32_t)version.patchVersion;
    }
}

ApogeeMtlQueue* apogee_mtl_queue_create(ApogeeMtlDevice* device)
{
    @autoreleasepool
    {
        return HANDLE(ApogeeMtlQueue, [GET(id<MTLDevice>, device) newCommandQueue]);
    }
}

// ---- Buffers ----------------------------------------------------------------------------------------------------------------------

ApogeeMtlBuffer* apogee_mtl_buffer_create(ApogeeMtlDevice* device, uint64_t length, int32_t storage, int32_t write_combined)
{
    @autoreleasepool
    {
        MTLResourceOptions options = storage == APOGEE_MTL_STORAGE_PRIVATE ? MTLResourceStorageModePrivate
            : storage == APOGEE_MTL_STORAGE_MANAGED ? MTLResourceStorageModeManaged
            : MTLResourceStorageModeShared;
        if (write_combined != 0 && storage != APOGEE_MTL_STORAGE_PRIVATE)
        {
            options |= MTLResourceCPUCacheModeWriteCombined;
        }

        return HANDLE(ApogeeMtlBuffer, [GET(id<MTLDevice>, device) newBufferWithLength:(NSUInteger)length options:options]);
    }
}

void* apogee_mtl_buffer_contents(const ApogeeMtlBuffer* buffer)
{
    return [GET(id<MTLBuffer>, buffer) contents];
}

uint64_t apogee_mtl_buffer_gpu_address(const ApogeeMtlBuffer* buffer)
{
    return [GET(id<MTLBuffer>, buffer) gpuAddress];
}

uint64_t apogee_mtl_buffer_length(const ApogeeMtlBuffer* buffer)
{
    return [GET(id<MTLBuffer>, buffer) length];
}

void apogee_mtl_buffer_did_modify_range(ApogeeMtlBuffer* buffer, uint64_t offset, uint64_t length)
{
    id<MTLBuffer> object = GET(id<MTLBuffer>, buffer);
    if (object.storageMode == MTLStorageModeManaged)
    {
        [object didModifyRange:NSMakeRange((NSUInteger)offset, (NSUInteger)length)];
    }
}

void apogee_mtl_buffer_set_label(ApogeeMtlBuffer* buffer, const char* label)
{
    @autoreleasepool
    {
        GET(id<MTLBuffer>, buffer).label = [NSString stringWithUTF8String:label];
    }
}

// The engine's PixelFormat (Vulkan's VkFormat numbers) as an MTLPixelFormat, by Metal's own names; 0 (invalid) for a format Metal does not have.
static MTLPixelFormat PixelFormatFor(int32_t engine_format)
{
    switch (engine_format)
    {
        case 9: return MTLPixelFormatR8Unorm; // R8Unorm
        case 10: return MTLPixelFormatR8Snorm; // R8Snorm
        case 13: return MTLPixelFormatR8Uint; // R8Uint
        case 14: return MTLPixelFormatR8Sint; // R8Sint
        case 15: return MTLPixelFormatR8Unorm_sRGB; // R8Srgb
        case 16: return MTLPixelFormatRG8Unorm; // R8G8Unorm
        case 17: return MTLPixelFormatRG8Snorm; // R8G8Snorm
        case 20: return MTLPixelFormatRG8Uint; // R8G8Uint
        case 21: return MTLPixelFormatRG8Sint; // R8G8Sint
        case 22: return MTLPixelFormatRG8Unorm_sRGB; // R8G8Srgb
        case 37: return MTLPixelFormatRGBA8Unorm; // R8G8B8A8Unorm
        case 38: return MTLPixelFormatRGBA8Snorm; // R8G8B8A8Snorm
        case 41: return MTLPixelFormatRGBA8Uint; // R8G8B8A8Uint
        case 42: return MTLPixelFormatRGBA8Sint; // R8G8B8A8Sint
        case 43: return MTLPixelFormatRGBA8Unorm_sRGB; // R8G8B8A8Srgb
        case 44: return MTLPixelFormatBGRA8Unorm; // B8G8R8A8Unorm
        case 50: return MTLPixelFormatBGRA8Unorm_sRGB; // B8G8R8A8Srgb
        case 64: return MTLPixelFormatRGB10A2Unorm; // A2B10G10R10UnormPack32
        case 122: return MTLPixelFormatRG11B10Float; // B10G11R11UfloatPack32
        case 70: return MTLPixelFormatR16Unorm; // R16Unorm
        case 71: return MTLPixelFormatR16Snorm; // R16Snorm
        case 74: return MTLPixelFormatR16Uint; // R16Uint
        case 75: return MTLPixelFormatR16Sint; // R16Sint
        case 76: return MTLPixelFormatR16Float; // R16Sfloat
        case 77: return MTLPixelFormatRG16Unorm; // R16G16Unorm
        case 78: return MTLPixelFormatRG16Snorm; // R16G16Snorm
        case 81: return MTLPixelFormatRG16Uint; // R16G16Uint
        case 82: return MTLPixelFormatRG16Sint; // R16G16Sint
        case 83: return MTLPixelFormatRG16Float; // R16G16Sfloat
        case 91: return MTLPixelFormatRGBA16Unorm; // R16G16B16A16Unorm
        case 92: return MTLPixelFormatRGBA16Snorm; // R16G16B16A16Snorm
        case 95: return MTLPixelFormatRGBA16Uint; // R16G16B16A16Uint
        case 96: return MTLPixelFormatRGBA16Sint; // R16G16B16A16Sint
        case 97: return MTLPixelFormatRGBA16Float; // R16G16B16A16Sfloat
        case 98: return MTLPixelFormatR32Uint; // R32Uint
        case 99: return MTLPixelFormatR32Sint; // R32Sint
        case 100: return MTLPixelFormatR32Float; // R32Sfloat
        case 101: return MTLPixelFormatRG32Uint; // R32G32Uint
        case 102: return MTLPixelFormatRG32Sint; // R32G32Sint
        case 103: return MTLPixelFormatRG32Float; // R32G32Sfloat
        case 107: return MTLPixelFormatRGBA32Uint; // R32G32B32A32Uint
        case 108: return MTLPixelFormatRGBA32Sint; // R32G32B32A32Sint
        case 109: return MTLPixelFormatRGBA32Float; // R32G32B32A32Sfloat
        case 124: return MTLPixelFormatDepth16Unorm; // D16Unorm
        case 126: return MTLPixelFormatDepth32Float; // D32Sfloat
        case 127: return MTLPixelFormatStencil8; // S8Uint
        case 129: return MTLPixelFormatDepth24Unorm_Stencil8; // D24UnormS8Uint
        case 130: return MTLPixelFormatDepth32Float_Stencil8; // D32SfloatS8Uint
        case 131: return MTLPixelFormatBC1_RGBA; // Bc1RgbUnormBlock
        case 132: return MTLPixelFormatBC1_RGBA_sRGB; // Bc1RgbSrgbBlock
        case 133: return MTLPixelFormatBC1_RGBA; // Bc1RgbaUnormBlock
        case 134: return MTLPixelFormatBC1_RGBA_sRGB; // Bc1RgbaSrgbBlock
        case 135: return MTLPixelFormatBC2_RGBA; // Bc2UnormBlock
        case 136: return MTLPixelFormatBC2_RGBA_sRGB; // Bc2SrgbBlock
        case 137: return MTLPixelFormatBC3_RGBA; // Bc3UnormBlock
        case 138: return MTLPixelFormatBC3_RGBA_sRGB; // Bc3SrgbBlock
        case 139: return MTLPixelFormatBC4_RUnorm; // Bc4UnormBlock
        case 140: return MTLPixelFormatBC4_RSnorm; // Bc4SnormBlock
        case 141: return MTLPixelFormatBC5_RGUnorm; // Bc5UnormBlock
        case 142: return MTLPixelFormatBC5_RGSnorm; // Bc5SnormBlock
        case 143: return MTLPixelFormatBC6H_RGBUfloat; // Bc6HUfloatBlock
        case 144: return MTLPixelFormatBC6H_RGBFloat; // Bc6HSfloatBlock
        case 145: return MTLPixelFormatBC7_RGBAUnorm; // Bc7UnormBlock
        case 146: return MTLPixelFormatBC7_RGBAUnorm_sRGB; // Bc7SrgbBlock
        case 157: return MTLPixelFormatASTC_4x4_LDR; // Astc4X4UnormBlock
        case 158: return MTLPixelFormatASTC_4x4_sRGB; // Astc4X4SrgbBlock
        case 159: return MTLPixelFormatASTC_5x4_LDR; // Astc5X4UnormBlock
        case 160: return MTLPixelFormatASTC_5x4_sRGB; // Astc5X4SrgbBlock
        case 161: return MTLPixelFormatASTC_5x5_LDR; // Astc5X5UnormBlock
        case 162: return MTLPixelFormatASTC_5x5_sRGB; // Astc5X5SrgbBlock
        case 163: return MTLPixelFormatASTC_6x5_LDR; // Astc6X5UnormBlock
        case 164: return MTLPixelFormatASTC_6x5_sRGB; // Astc6X5SrgbBlock
        case 165: return MTLPixelFormatASTC_6x6_LDR; // Astc6X6UnormBlock
        case 166: return MTLPixelFormatASTC_6x6_sRGB; // Astc6X6SrgbBlock
        case 167: return MTLPixelFormatASTC_8x5_LDR; // Astc8X5UnormBlock
        case 168: return MTLPixelFormatASTC_8x5_sRGB; // Astc8X5SrgbBlock
        case 169: return MTLPixelFormatASTC_8x6_LDR; // Astc8X6UnormBlock
        case 170: return MTLPixelFormatASTC_8x6_sRGB; // Astc8X6SrgbBlock
        case 171: return MTLPixelFormatASTC_8x8_LDR; // Astc8X8UnormBlock
        case 172: return MTLPixelFormatASTC_8x8_sRGB; // Astc8X8SrgbBlock
        case 173: return MTLPixelFormatASTC_10x5_LDR; // Astc10X5UnormBlock
        case 174: return MTLPixelFormatASTC_10x5_sRGB; // Astc10X5SrgbBlock
        case 175: return MTLPixelFormatASTC_10x6_LDR; // Astc10X6UnormBlock
        case 176: return MTLPixelFormatASTC_10x6_sRGB; // Astc10X6SrgbBlock
        case 177: return MTLPixelFormatASTC_10x8_LDR; // Astc10X8UnormBlock
        case 178: return MTLPixelFormatASTC_10x8_sRGB; // Astc10X8SrgbBlock
        case 179: return MTLPixelFormatASTC_10x10_LDR; // Astc10X10UnormBlock
        case 180: return MTLPixelFormatASTC_10x10_sRGB; // Astc10X10SrgbBlock
        case 181: return MTLPixelFormatASTC_12x10_LDR; // Astc12X10UnormBlock
        case 182: return MTLPixelFormatASTC_12x10_sRGB; // Astc12X10SrgbBlock
        case 183: return MTLPixelFormatASTC_12x12_LDR; // Astc12X12UnormBlock
        case 184: return MTLPixelFormatASTC_12x12_sRGB; // Astc12X12SrgbBlock
        default: return MTLPixelFormatInvalid;
    }
}

uint32_t apogee_mtl_pixel_format(int32_t engine_format)
{
    return (uint32_t)PixelFormatFor(engine_format);
}

// ---- Textures and samplers -------------------------------------------------------------------------------------------------------

static MTLTextureType TextureType(int32_t type, uint32_t sample_count)
{
    switch (type)
    {
        case APOGEE_MTL_TEXTURE_2D_ARRAY: return MTLTextureType2DArray;
        case APOGEE_MTL_TEXTURE_CUBE: return MTLTextureTypeCube;
        case APOGEE_MTL_TEXTURE_CUBE_ARRAY: return MTLTextureTypeCubeArray;
        case APOGEE_MTL_TEXTURE_3D: return MTLTextureType3D;
        default: return sample_count > 1 ? MTLTextureType2DMultisample : MTLTextureType2D;
    }
}

ApogeeMtlTexture* apogee_mtl_texture_create(ApogeeMtlDevice* device, const ApogeeMtlTextureDesc* desc)
{
    @autoreleasepool
    {
        MTLTextureDescriptor* descriptor = [[MTLTextureDescriptor alloc] init];
        descriptor.textureType = TextureType(desc->type, desc->sample_count);
        descriptor.pixelFormat = (MTLPixelFormat)desc->pixel_format;
        descriptor.width = desc->width;
        descriptor.height = desc->height;
        descriptor.depth = desc->depth > 0 ? desc->depth : 1;
        descriptor.mipmapLevelCount = desc->mip_levels > 0 ? desc->mip_levels : 1;
        descriptor.arrayLength = desc->array_length > 0 ? desc->array_length : 1;
        descriptor.sampleCount = desc->sample_count > 0 ? desc->sample_count : 1;
        descriptor.storageMode = MTLStorageModePrivate;
        MTLTextureUsage usage = 0;
        usage |= (desc->usage & APOGEE_MTL_USAGE_SHADER_READ) != 0 ? MTLTextureUsageShaderRead : 0;
        usage |= (desc->usage & APOGEE_MTL_USAGE_SHADER_WRITE) != 0 ? MTLTextureUsageShaderWrite : 0;
        usage |= (desc->usage & APOGEE_MTL_USAGE_RENDER_TARGET) != 0 ? MTLTextureUsageRenderTarget : 0;
        usage |= (desc->usage & APOGEE_MTL_USAGE_PIXEL_FORMAT_VIEW) != 0 ? MTLTextureUsagePixelFormatView : 0;
        descriptor.usage = usage;
        return HANDLE(ApogeeMtlTexture, [GET(id<MTLDevice>, device) newTextureWithDescriptor:descriptor]);
    }
}

ApogeeMtlTexture* apogee_mtl_texture_view(
    ApogeeMtlTexture* texture, uint32_t pixel_format, int32_t type, uint32_t base_level, uint32_t level_count, uint32_t base_slice, uint32_t slice_count)
{
    @autoreleasepool
    {
        id<MTLTexture> source = GET(id<MTLTexture>, texture);
        MTLPixelFormat format = pixel_format != 0 ? (MTLPixelFormat)pixel_format : source.pixelFormat;
        return HANDLE(ApogeeMtlTexture, [source newTextureViewWithPixelFormat:format
                                                                  textureType:TextureType(type, 1)
                                                                       levels:NSMakeRange(base_level, level_count)
                                                                       slices:NSMakeRange(base_slice, slice_count)]);
    }
}

uint64_t apogee_mtl_texture_resource_id(const ApogeeMtlTexture* texture)
{
    MTLResourceID identifier = [GET(id<MTLTexture>, texture) gpuResourceID];
    return identifier._impl;
}

void apogee_mtl_texture_set_label(ApogeeMtlTexture* texture, const char* label)
{
    @autoreleasepool
    {
        GET(id<MTLTexture>, texture).label = [NSString stringWithUTF8String:label];
    }
}

static MTLSamplerAddressMode AddressMode(int32_t mode)
{
    switch (mode)
    {
        case APOGEE_MTL_ADDRESS_MIRRORED_REPEAT: return MTLSamplerAddressModeMirrorRepeat;
        case APOGEE_MTL_ADDRESS_CLAMP_TO_EDGE: return MTLSamplerAddressModeClampToEdge;
        default: return MTLSamplerAddressModeRepeat;
    }
}

ApogeeMtlSampler* apogee_mtl_sampler_create(ApogeeMtlDevice* device, const ApogeeMtlSamplerDesc* desc)
{
    @autoreleasepool
    {
        MTLSamplerDescriptor* descriptor = [[MTLSamplerDescriptor alloc] init];
        MTLSamplerMinMagFilter filter = desc->filter == APOGEE_MTL_FILTER_NEAREST ? MTLSamplerMinMagFilterNearest : MTLSamplerMinMagFilterLinear;
        descriptor.minFilter = filter;
        descriptor.magFilter = filter;
        descriptor.mipFilter = desc->mip_filter == APOGEE_MTL_FILTER_NEAREST ? MTLSamplerMipFilterNearest : MTLSamplerMipFilterLinear;
        descriptor.sAddressMode = AddressMode(desc->address_mode);
        descriptor.tAddressMode = AddressMode(desc->address_mode);
        descriptor.rAddressMode = AddressMode(desc->address_mode);
        descriptor.maxAnisotropy = desc->max_anisotropy > 1 ? (NSUInteger)desc->max_anisotropy : 1;
        descriptor.compareFunction = desc->compare != 0 ? MTLCompareFunctionGreaterEqual : MTLCompareFunctionNever;
        descriptor.lodMinClamp = 0.0f;
        descriptor.lodMaxClamp = FLT_MAX;
        descriptor.supportArgumentBuffers = YES;
        return HANDLE(ApogeeMtlSampler, [GET(id<MTLDevice>, device) newSamplerStateWithDescriptor:descriptor]);
    }
}

uint64_t apogee_mtl_sampler_resource_id(const ApogeeMtlSampler* sampler)
{
    MTLResourceID identifier = [GET(id<MTLSamplerState>, sampler) gpuResourceID];
    return identifier._impl;
}

// ---- Shaders and pipelines ---------------------------------------------------------------------------------------------------------

ApogeeMtlLibrary* apogee_mtl_library_create(ApogeeMtlDevice* device, const char* source, char* error, int32_t error_capacity)
{
    @autoreleasepool
    {
        MTLCompileOptions* options = [[MTLCompileOptions alloc] init];
        options.languageVersion = MTLLanguageVersion3_1;
        NSError* failure = nil;
        id<MTLLibrary> library = [GET(id<MTLDevice>, device) newLibraryWithSource:[NSString stringWithUTF8String:source] options:options error:&failure];
        if (library == nil)
        {
            CopyError(failure, error, error_capacity);
            return NULL;
        }

        return HANDLE(ApogeeMtlLibrary, library);
    }
}

static id<MTLFunction> Function(ApogeeMtlLibrary* library, const char* name, char* error, int32_t error_capacity)
{
    id<MTLFunction> function = [GET(id<MTLLibrary>, library) newFunctionWithName:[NSString stringWithUTF8String:name]];
    if (function == nil && error != NULL && error_capacity > 0)
    {
        snprintf(error, (size_t)error_capacity, "the library has no function named '%s'", name);
    }

    return function;
}

static void SetBlend(MTLRenderPipelineColorAttachmentDescriptor* attachment, int32_t blend)
{
    if (blend == APOGEE_MTL_BLEND_OPAQUE)
    {
        attachment.blendingEnabled = NO;
        return;
    }

    attachment.blendingEnabled = YES;
    attachment.rgbBlendOperation = MTLBlendOperationAdd;
    attachment.alphaBlendOperation = MTLBlendOperationAdd;
    switch (blend)
    {
        case APOGEE_MTL_BLEND_ALPHA:
            attachment.sourceRGBBlendFactor = MTLBlendFactorSourceAlpha;
            attachment.destinationRGBBlendFactor = MTLBlendFactorOneMinusSourceAlpha;
            attachment.sourceAlphaBlendFactor = MTLBlendFactorOne;
            attachment.destinationAlphaBlendFactor = MTLBlendFactorOneMinusSourceAlpha;
            break;
        case APOGEE_MTL_BLEND_PREMULTIPLIED:
            attachment.sourceRGBBlendFactor = MTLBlendFactorOne;
            attachment.destinationRGBBlendFactor = MTLBlendFactorOneMinusSourceAlpha;
            attachment.sourceAlphaBlendFactor = MTLBlendFactorOne;
            attachment.destinationAlphaBlendFactor = MTLBlendFactorOneMinusSourceAlpha;
            break;
        default:
            attachment.sourceRGBBlendFactor = MTLBlendFactorOne;
            attachment.destinationRGBBlendFactor = MTLBlendFactorOne;
            attachment.sourceAlphaBlendFactor = MTLBlendFactorOne;
            attachment.destinationAlphaBlendFactor = MTLBlendFactorOne;
            break;
    }
}

ApogeeMtlRenderPipeline* apogee_mtl_render_pipeline_create(
    ApogeeMtlDevice* device, ApogeeMtlLibrary* vertex_library, const char* vertex_name, ApogeeMtlLibrary* fragment_library, const char* fragment_name,
    const ApogeeMtlRenderPipelineDesc* desc, char* error, int32_t error_capacity)
{
    @autoreleasepool
    {
        MTLRenderPipelineDescriptor* descriptor = [[MTLRenderPipelineDescriptor alloc] init];
        descriptor.vertexFunction = Function(vertex_library, vertex_name, error, error_capacity);
        if (descriptor.vertexFunction == nil)
        {
            return NULL;
        }

        if (fragment_name != NULL)
        {
            descriptor.fragmentFunction = Function(fragment_library, fragment_name, error, error_capacity);
            if (descriptor.fragmentFunction == nil)
            {
                return NULL;
            }
        }

        for (int32_t i = 0; i < desc->color_count && i < 8; i++)
        {
            descriptor.colorAttachments[(NSUInteger)i].pixelFormat = (MTLPixelFormat)desc->color_formats[i];
            SetBlend(descriptor.colorAttachments[(NSUInteger)i], desc->blend);
        }

        descriptor.depthAttachmentPixelFormat = (MTLPixelFormat)desc->depth_format;
        descriptor.stencilAttachmentPixelFormat = (MTLPixelFormat)desc->stencil_format;
        descriptor.rasterSampleCount = desc->sample_count > 0 ? (NSUInteger)desc->sample_count : 1;
        descriptor.inputPrimitiveTopology = (MTLPrimitiveTopologyClass)desc->topology_class;

        NSError* failure = nil;
        id<MTLRenderPipelineState> state = [GET(id<MTLDevice>, device) newRenderPipelineStateWithDescriptor:descriptor error:&failure];
        if (state == nil)
        {
            CopyError(failure, error, error_capacity);
            return NULL;
        }

        return HANDLE(ApogeeMtlRenderPipeline, state);
    }
}

ApogeeMtlComputePipeline* apogee_mtl_compute_pipeline_create(ApogeeMtlDevice* device, ApogeeMtlLibrary* library, const char* name, char* error, int32_t error_capacity)
{
    @autoreleasepool
    {
        id<MTLFunction> function = Function(library, name, error, error_capacity);
        if (function == nil)
        {
            return NULL;
        }

        NSError* failure = nil;
        id<MTLComputePipelineState> state = [GET(id<MTLDevice>, device) newComputePipelineStateWithFunction:function error:&failure];
        if (state == nil)
        {
            CopyError(failure, error, error_capacity);
            return NULL;
        }

        return HANDLE(ApogeeMtlComputePipeline, state);
    }
}

ApogeeMtlDepthStencil* apogee_mtl_depth_stencil_create(ApogeeMtlDevice* device, int32_t compare, int32_t write_enabled)
{
    @autoreleasepool
    {
        MTLDepthStencilDescriptor* descriptor = [[MTLDepthStencilDescriptor alloc] init];
        descriptor.depthCompareFunction = (MTLCompareFunction)compare; // the engine's CompareOp numbers are Metal's
        descriptor.depthWriteEnabled = write_enabled != 0;
        return HANDLE(ApogeeMtlDepthStencil, [GET(id<MTLDevice>, device) newDepthStencilStateWithDescriptor:descriptor]);
    }
}

// ---- Residency --------------------------------------------------------------------------------------------------------------------

ApogeeMtlResidencySet* apogee_mtl_residency_set_create(ApogeeMtlDevice* device, ApogeeMtlQueue* queue)
{
    if (@available(macOS 15.0, *))
    {
        @autoreleasepool
        {
            MTLResidencySetDescriptor* descriptor = [[MTLResidencySetDescriptor alloc] init];
            descriptor.initialCapacity = 4096;
            NSError* failure = nil;
            id<MTLResidencySet> set = [GET(id<MTLDevice>, device) newResidencySetWithDescriptor:descriptor error:&failure];
            if (set == nil)
            {
                return NULL;
            }

            [GET(id<MTLCommandQueue>, queue) addResidencySet:set];
            return HANDLE(ApogeeMtlResidencySet, set);
        }
    }

    return NULL;
}

void apogee_mtl_residency_set_add_buffer(ApogeeMtlResidencySet* set, ApogeeMtlBuffer* buffer)
{
    if (@available(macOS 15.0, *))
    {
        [GET(id<MTLResidencySet>, set) addAllocation:GET(id<MTLBuffer>, buffer)];
    }
}

void apogee_mtl_residency_set_add_texture(ApogeeMtlResidencySet* set, ApogeeMtlTexture* texture)
{
    if (@available(macOS 15.0, *))
    {
        [GET(id<MTLResidencySet>, set) addAllocation:GET(id<MTLTexture>, texture)];
    }
}

void apogee_mtl_residency_set_remove_buffer(ApogeeMtlResidencySet* set, ApogeeMtlBuffer* buffer)
{
    if (@available(macOS 15.0, *))
    {
        [GET(id<MTLResidencySet>, set) removeAllocation:GET(id<MTLBuffer>, buffer)];
    }
}

void apogee_mtl_residency_set_remove_texture(ApogeeMtlResidencySet* set, ApogeeMtlTexture* texture)
{
    if (@available(macOS 15.0, *))
    {
        [GET(id<MTLResidencySet>, set) removeAllocation:GET(id<MTLTexture>, texture)];
    }
}

void apogee_mtl_residency_set_commit(ApogeeMtlResidencySet* set)
{
    if (@available(macOS 15.0, *))
    {
        [GET(id<MTLResidencySet>, set) commit];
    }
}

// ---- Command buffers and events -----------------------------------------------------------------------------------------------------

ApogeeMtlCommandBuffer* apogee_mtl_command_buffer_create(ApogeeMtlQueue* queue)
{
    @autoreleasepool
    {
        return HANDLE(ApogeeMtlCommandBuffer, [GET(id<MTLCommandQueue>, queue) commandBuffer]);
    }
}

void apogee_mtl_command_buffer_commit(ApogeeMtlCommandBuffer* command_buffer)
{
    [GET(id<MTLCommandBuffer>, command_buffer) commit];
}

void apogee_mtl_command_buffer_wait(ApogeeMtlCommandBuffer* command_buffer)
{
    [GET(id<MTLCommandBuffer>, command_buffer) waitUntilCompleted];
}

int32_t apogee_mtl_command_buffer_error(const ApogeeMtlCommandBuffer* command_buffer, char* error, int32_t error_capacity)
{
    @autoreleasepool
    {
        NSError* failure = GET(id<MTLCommandBuffer>, command_buffer).error;
        if (failure == nil)
        {
            return 0;
        }

        CopyError(failure, error, error_capacity);
        return failure.code != 0 ? (int32_t)failure.code : -1;
    }
}

void apogee_mtl_command_buffer_encode_signal(ApogeeMtlCommandBuffer* command_buffer, ApogeeMtlEvent* event, uint64_t value)
{
    [GET(id<MTLCommandBuffer>, command_buffer) encodeSignalEvent:GET(id<MTLSharedEvent>, event) value:value];
}

void apogee_mtl_command_buffer_present(ApogeeMtlCommandBuffer* command_buffer, ApogeeMtlDrawable* drawable)
{
    [GET(id<MTLCommandBuffer>, command_buffer) presentDrawable:GET(id<MTLDrawable>, drawable)];
}

void apogee_mtl_command_buffer_push_debug_group(ApogeeMtlCommandBuffer* command_buffer, const char* name)
{
    @autoreleasepool
    {
        [GET(id<MTLCommandBuffer>, command_buffer) pushDebugGroup:[NSString stringWithUTF8String:name]];
    }
}

void apogee_mtl_command_buffer_pop_debug_group(ApogeeMtlCommandBuffer* command_buffer)
{
    [GET(id<MTLCommandBuffer>, command_buffer) popDebugGroup];
}

ApogeeMtlEvent* apogee_mtl_event_create(ApogeeMtlDevice* device)
{
    @autoreleasepool
    {
        return HANDLE(ApogeeMtlEvent, [GET(id<MTLDevice>, device) newSharedEvent]);
    }
}

uint64_t apogee_mtl_event_signaled_value(const ApogeeMtlEvent* event)
{
    return GET(id<MTLSharedEvent>, event).signaledValue;
}

int32_t apogee_mtl_event_wait(ApogeeMtlEvent* event, uint64_t value, uint64_t timeout_ms)
{
    return [GET(id<MTLSharedEvent>, event) waitUntilSignaledValue:value timeoutMS:timeout_ms] ? 0 : 1;
}

// ---- Encoders -------------------------------------------------------------------------------------------------------------------------

ApogeeMtlFence* apogee_mtl_fence_create(ApogeeMtlDevice* device)
{
    @autoreleasepool
    {
        return HANDLE(ApogeeMtlFence, [GET(id<MTLDevice>, device) newFence]);
    }
}

ApogeeMtlComputeEncoder* apogee_mtl_compute_encoder_begin(ApogeeMtlCommandBuffer* command_buffer)
{
    @autoreleasepool
    {
        return HANDLE(ApogeeMtlComputeEncoder, [GET(id<MTLCommandBuffer>, command_buffer) computeCommandEncoder]);
    }
}

void apogee_mtl_compute_encoder_set_pipeline(ApogeeMtlComputeEncoder* encoder, ApogeeMtlComputePipeline* pipeline)
{
    [GET(id<MTLComputeCommandEncoder>, encoder) setComputePipelineState:GET(id<MTLComputePipelineState>, pipeline)];
}

void apogee_mtl_compute_encoder_set_bytes(ApogeeMtlComputeEncoder* encoder, const void* bytes, uint32_t length, uint32_t index)
{
    [GET(id<MTLComputeCommandEncoder>, encoder) setBytes:bytes length:length atIndex:index];
}

void apogee_mtl_compute_encoder_set_buffer(ApogeeMtlComputeEncoder* encoder, ApogeeMtlBuffer* buffer, uint64_t offset, uint32_t index)
{
    [GET(id<MTLComputeCommandEncoder>, encoder) setBuffer:GET(id<MTLBuffer>, buffer) offset:(NSUInteger)offset atIndex:index];
}

void apogee_mtl_compute_encoder_wait_fence(ApogeeMtlComputeEncoder* encoder, ApogeeMtlFence* fence)
{
    [GET(id<MTLComputeCommandEncoder>, encoder) waitForFence:GET(id<MTLFence>, fence)];
}

void apogee_mtl_compute_encoder_update_fence(ApogeeMtlComputeEncoder* encoder, ApogeeMtlFence* fence)
{
    [GET(id<MTLComputeCommandEncoder>, encoder) updateFence:GET(id<MTLFence>, fence)];
}

void apogee_mtl_compute_encoder_dispatch(
    ApogeeMtlComputeEncoder* encoder, uint32_t groups_x, uint32_t groups_y, uint32_t groups_z, uint32_t threads_x, uint32_t threads_y, uint32_t threads_z)
{
    [GET(id<MTLComputeCommandEncoder>, encoder) dispatchThreadgroups:MTLSizeMake(groups_x, groups_y, groups_z)
                                               threadsPerThreadgroup:MTLSizeMake(threads_x, threads_y, threads_z)];
}

void apogee_mtl_compute_encoder_end(ApogeeMtlComputeEncoder* encoder)
{
    [GET(id<MTLComputeCommandEncoder>, encoder) endEncoding];
    CFRelease(encoder);
}

ApogeeMtlBlitEncoder* apogee_mtl_blit_encoder_begin(ApogeeMtlCommandBuffer* command_buffer)
{
    @autoreleasepool
    {
        return HANDLE(ApogeeMtlBlitEncoder, [GET(id<MTLCommandBuffer>, command_buffer) blitCommandEncoder]);
    }
}

void apogee_mtl_blit_encoder_wait_fence(ApogeeMtlBlitEncoder* encoder, ApogeeMtlFence* fence)
{
    [GET(id<MTLBlitCommandEncoder>, encoder) waitForFence:GET(id<MTLFence>, fence)];
}

void apogee_mtl_blit_encoder_update_fence(ApogeeMtlBlitEncoder* encoder, ApogeeMtlFence* fence)
{
    [GET(id<MTLBlitCommandEncoder>, encoder) updateFence:GET(id<MTLFence>, fence)];
}

void apogee_mtl_blit_encoder_copy_buffer(
    ApogeeMtlBlitEncoder* encoder, ApogeeMtlBuffer* source, uint64_t source_offset, ApogeeMtlBuffer* destination, uint64_t destination_offset, uint64_t size)
{
    [GET(id<MTLBlitCommandEncoder>, encoder) copyFromBuffer:GET(id<MTLBuffer>, source)
                                               sourceOffset:(NSUInteger)source_offset
                                                   toBuffer:GET(id<MTLBuffer>, destination)
                                          destinationOffset:(NSUInteger)destination_offset
                                                       size:(NSUInteger)size];
}

void apogee_mtl_blit_encoder_copy_buffer_to_texture(
    ApogeeMtlBlitEncoder* encoder, ApogeeMtlBuffer* source, uint64_t source_offset, uint32_t bytes_per_row, uint32_t bytes_per_image,
    ApogeeMtlTexture* texture, uint32_t level, uint32_t slice, uint32_t width, uint32_t height, uint32_t depth)
{
    [GET(id<MTLBlitCommandEncoder>, encoder) copyFromBuffer:GET(id<MTLBuffer>, source)
                                               sourceOffset:(NSUInteger)source_offset
                                          sourceBytesPerRow:bytes_per_row
                                        sourceBytesPerImage:bytes_per_image
                                                 sourceSize:MTLSizeMake(width, height, depth)
                                                  toTexture:GET(id<MTLTexture>, texture)
                                           destinationSlice:slice
                                           destinationLevel:level
                                          destinationOrigin:MTLOriginMake(0, 0, 0)];
}

void apogee_mtl_blit_encoder_copy_texture_to_buffer(
    ApogeeMtlBlitEncoder* encoder, ApogeeMtlTexture* texture, uint32_t level, uint32_t slice, uint32_t width, uint32_t height, uint32_t depth,
    ApogeeMtlBuffer* destination, uint64_t destination_offset, uint32_t bytes_per_row, uint32_t bytes_per_image)
{
    [GET(id<MTLBlitCommandEncoder>, encoder) copyFromTexture:GET(id<MTLTexture>, texture)
                                                 sourceSlice:slice
                                                 sourceLevel:level
                                                sourceOrigin:MTLOriginMake(0, 0, 0)
                                                  sourceSize:MTLSizeMake(width, height, depth)
                                                    toBuffer:GET(id<MTLBuffer>, destination)
                                           destinationOffset:(NSUInteger)destination_offset
                                      destinationBytesPerRow:bytes_per_row
                                    destinationBytesPerImage:bytes_per_image];
}

void apogee_mtl_blit_encoder_end(ApogeeMtlBlitEncoder* encoder)
{
    [GET(id<MTLBlitCommandEncoder>, encoder) endEncoding];
    CFRelease(encoder);
}

ApogeeMtlRenderEncoder* apogee_mtl_render_encoder_begin(
    ApogeeMtlCommandBuffer* command_buffer, const ApogeeMtlColorAttachment* colors, int32_t color_count, const ApogeeMtlDepthAttachment* depth)
{
    @autoreleasepool
    {
        MTLRenderPassDescriptor* pass = [MTLRenderPassDescriptor renderPassDescriptor];
        for (int32_t i = 0; i < color_count && i < 8; i++)
        {
            MTLRenderPassColorAttachmentDescriptor* attachment = pass.colorAttachments[(NSUInteger)i];
            attachment.texture = GET(id<MTLTexture>, colors[i].texture);
            attachment.loadAction = colors[i].clear != 0 ? MTLLoadActionClear : MTLLoadActionLoad;
            attachment.storeAction = colors[i].store != 0 ? MTLStoreActionStore : MTLStoreActionDontCare;
            attachment.clearColor = MTLClearColorMake(colors[i].clear_color[0], colors[i].clear_color[1], colors[i].clear_color[2], colors[i].clear_color[3]);
        }

        if (depth != NULL)
        {
            pass.depthAttachment.texture = GET(id<MTLTexture>, depth->texture);
            pass.depthAttachment.loadAction = depth->clear != 0 ? MTLLoadActionClear : MTLLoadActionLoad;
            pass.depthAttachment.storeAction = depth->store != 0 ? MTLStoreActionStore : MTLStoreActionDontCare;
            pass.depthAttachment.clearDepth = depth->clear_depth;
        }

        return HANDLE(ApogeeMtlRenderEncoder, [GET(id<MTLCommandBuffer>, command_buffer) renderCommandEncoderWithDescriptor:pass]);
    }
}

void apogee_mtl_render_encoder_wait_fence(ApogeeMtlRenderEncoder* encoder, ApogeeMtlFence* fence)
{
    [GET(id<MTLRenderCommandEncoder>, encoder) waitForFence:GET(id<MTLFence>, fence) beforeStages:MTLRenderStageVertex];
}

void apogee_mtl_render_encoder_update_fence(ApogeeMtlRenderEncoder* encoder, ApogeeMtlFence* fence)
{
    [GET(id<MTLRenderCommandEncoder>, encoder) updateFence:GET(id<MTLFence>, fence) afterStages:MTLRenderStageFragment];
}

void apogee_mtl_render_encoder_set_pipeline(ApogeeMtlRenderEncoder* encoder, ApogeeMtlRenderPipeline* pipeline)
{
    [GET(id<MTLRenderCommandEncoder>, encoder) setRenderPipelineState:GET(id<MTLRenderPipelineState>, pipeline)];
}

void apogee_mtl_render_encoder_set_depth_stencil(ApogeeMtlRenderEncoder* encoder, ApogeeMtlDepthStencil* state)
{
    [GET(id<MTLRenderCommandEncoder>, encoder) setDepthStencilState:GET(id<MTLDepthStencilState>, state)];
}

void apogee_mtl_render_encoder_set_rasterizer(ApogeeMtlRenderEncoder* encoder, int32_t cull, int32_t front_ccw)
{
    id<MTLRenderCommandEncoder> object = GET(id<MTLRenderCommandEncoder>, encoder);
    [object setCullMode:(MTLCullMode)cull];
    [object setFrontFacingWinding:front_ccw != 0 ? MTLWindingCounterClockwise : MTLWindingClockwise];
}

void apogee_mtl_render_encoder_set_viewport(ApogeeMtlRenderEncoder* encoder, double x, double y, double width, double height, double near_depth, double far_depth)
{
    MTLViewport viewport = {x, y, width, height, near_depth, far_depth};
    [GET(id<MTLRenderCommandEncoder>, encoder) setViewport:viewport];
}

void apogee_mtl_render_encoder_set_scissor(ApogeeMtlRenderEncoder* encoder, uint32_t x, uint32_t y, uint32_t width, uint32_t height)
{
    MTLScissorRect rect = {x, y, width, height};
    [GET(id<MTLRenderCommandEncoder>, encoder) setScissorRect:rect];
}

void apogee_mtl_render_encoder_set_bytes(ApogeeMtlRenderEncoder* encoder, const void* bytes, uint32_t length, uint32_t index)
{
    id<MTLRenderCommandEncoder> object = GET(id<MTLRenderCommandEncoder>, encoder);
    [object setVertexBytes:bytes length:length atIndex:index];
    [object setFragmentBytes:bytes length:length atIndex:index];
}

void apogee_mtl_render_encoder_set_fragment_buffer(ApogeeMtlRenderEncoder* encoder, ApogeeMtlBuffer* buffer, uint64_t offset, uint32_t index)
{
    [GET(id<MTLRenderCommandEncoder>, encoder) setFragmentBuffer:GET(id<MTLBuffer>, buffer) offset:(NSUInteger)offset atIndex:index];
}

void apogee_mtl_render_encoder_draw(
    ApogeeMtlRenderEncoder* encoder, int32_t primitive, uint32_t vertex_start, uint32_t vertex_count, uint32_t instance_count, uint32_t base_instance)
{
    [GET(id<MTLRenderCommandEncoder>, encoder) drawPrimitives:(MTLPrimitiveType)primitive
                                                  vertexStart:vertex_start
                                                  vertexCount:vertex_count
                                                instanceCount:instance_count
                                                 baseInstance:base_instance];
}

void apogee_mtl_render_encoder_draw_indexed(
    ApogeeMtlRenderEncoder* encoder, int32_t primitive, uint32_t index_count, int32_t index_type, ApogeeMtlBuffer* index_buffer, uint64_t index_offset,
    uint32_t instance_count, int32_t base_vertex, uint32_t base_instance)
{
    [GET(id<MTLRenderCommandEncoder>, encoder) drawIndexedPrimitives:(MTLPrimitiveType)primitive
                                                          indexCount:index_count
                                                           indexType:(MTLIndexType)index_type
                                                         indexBuffer:GET(id<MTLBuffer>, index_buffer)
                                                   indexBufferOffset:(NSUInteger)index_offset
                                                       instanceCount:instance_count
                                                          baseVertex:base_vertex
                                                        baseInstance:base_instance];
}

void apogee_mtl_render_encoder_end(ApogeeMtlRenderEncoder* encoder)
{
    [GET(id<MTLRenderCommandEncoder>, encoder) endEncoding];
    CFRelease(encoder);
}

// ---- The window -----------------------------------------------------------------------------------------------------------------------

ApogeeMtlLayer* apogee_mtl_layer_attach(void* sdl_layer, ApogeeMtlDevice* device, uint32_t width, uint32_t height, int32_t vsync)
{
    @autoreleasepool
    {
        CAMetalLayer* layer = (__bridge CAMetalLayer*)sdl_layer;
        layer.device = GET(id<MTLDevice>, device);
        layer.pixelFormat = MTLPixelFormatBGRA8Unorm;
        layer.framebufferOnly = NO;
        layer.drawableSize = CGSizeMake(width, height);
        layer.displaySyncEnabled = vsync != 0;
        layer.maximumDrawableCount = 3;
        return HANDLE(ApogeeMtlLayer, layer);
    }
}

void apogee_mtl_layer_resize(ApogeeMtlLayer* layer, uint32_t width, uint32_t height)
{
    GET(CAMetalLayer*, layer).drawableSize = CGSizeMake(width, height);
}

ApogeeMtlDrawable* apogee_mtl_layer_next_drawable(ApogeeMtlLayer* layer)
{
    @autoreleasepool
    {
        return HANDLE(ApogeeMtlDrawable, [GET(CAMetalLayer*, layer) nextDrawable]);
    }
}

ApogeeMtlTexture* apogee_mtl_drawable_texture(const ApogeeMtlDrawable* drawable)
{
    @autoreleasepool
    {
        return HANDLE(ApogeeMtlTexture, GET(id<CAMetalDrawable>, drawable).texture);
    }
}
