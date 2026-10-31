#pragma once

/* Stands in for a library that has its own binding assembly (Vulkan, for VMA). */
typedef struct OtherHandle_T* OtherHandle;
typedef struct OtherInfo { int Value; } OtherInfo;
typedef enum OtherKind { OTHER_KIND_A = 0, OTHER_KIND_B = 1 } OtherKind;
typedef unsigned int OtherFlags;
