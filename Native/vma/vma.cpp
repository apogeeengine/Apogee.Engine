// Vulkan Memory Allocator's implementation. The Vulkan functions are not linked: the caller passes vkGetInstanceProcAddr and
// vkGetDeviceProcAddr in VmaVulkanFunctions and VMA loads the rest through them.
#define VMA_STATIC_VULKAN_FUNCTIONS 0
#define VMA_DYNAMIC_VULKAN_FUNCTIONS 1
#define VMA_IMPLEMENTATION
#include "vk_mem_alloc.h"
