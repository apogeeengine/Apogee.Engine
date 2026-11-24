#pragma once
#include "other.h"

typedef enum OwnUsageFlagBits
{
    OWN_USAGE_READ_BIT = 0x1,
    OWN_USAGE_WRITE_BIT = 0x2,
    OWN_USAGE_FLAG_BITS_MAX_ENUM = 0x7FFFFFFF
} OwnUsageFlagBits;
typedef unsigned int OwnUsageFlags;

typedef struct OwnCreateInfo
{
    OtherHandle owner;
    const OtherInfo* info;
    OtherFlags flags;
    OtherKind kind;
    OwnUsageFlags usage;
} OwnCreateInfo;

/* C's long is 8 bytes on Linux and macOS and 4 on Windows: these structs' sizes depend on the platform. */
typedef struct OwnStream { long position; int flags; } OwnStream;
typedef struct OwnHolder { OwnStream stream; int other; } OwnHolder;

OtherHandle own_create(const OwnCreateInfo* createInfo, OtherInfo* outInfo);
void own_use(OtherKind kind, OtherInfo value, const OtherInfo* const* infos);

/* Exported variables: const ones are read-only references, the others writable; FLECS_ID…ID_ is Flecs' ecs_id(…) symbol. */
extern const unsigned long long own_limit;
extern int own_counter;
extern const unsigned long long FLECS_IDown_valueID_;
extern const int own_table[4];

/* A parameter written as an array is a pointer in C. */
void own_set_color(const char* name, float color[4], int grid[2][2], int unsized[]);

/**************************************************************************
 *
 * @function: own_banner
 *
 * The comment of FreeType's headers starts with a row of asterisks, which is not a description.
 */
void own_banner(int x);

/** Says hello. */
void own_hello(void);
