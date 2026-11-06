/*
 * Our C API over ImGuizmo (ThirdParty/imguizmo): 3D manipulation gizmos (move, rotate, scale) and a view cube, drawn with Dear ImGui. ImGuizmo is C++; this is plain functions over it, in the same shared
 * library as Dear ImGui (cimgui), so they act on ImGui's current context: make it current (igSetCurrentContext) before calling.
 *
 * Matrices are 16 floats, column-major as ImGuizmo has them. Operations and modes are the values below (a bit mask for operations). Rectangles and positions are in the screen's pixels.
 * Call agz_begin_frame once a frame after the ImGui frame has begun, agz_set_rect with the viewport's rectangle (and agz_set_drawlist if the gizmo is not to be drawn in the current window), then
 * agz_manipulate. None of it throws.
 */
#ifndef APOGEE_IMGUIZMO_H
#define APOGEE_IMGUIZMO_H

#include <stdbool.h>
#include <stdint.h>

#ifndef APOGEE_IMGUIZMO_API
#define APOGEE_IMGUIZMO_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/** The gizmo's operation: a bit mask (ImGuizmo::OPERATION). The combinations are the ones ImGuizmo names. */
typedef enum AgzOperation
{
    AGZ_TRANSLATE_X = 1 << 0,
    AGZ_TRANSLATE_Y = 1 << 1,
    AGZ_TRANSLATE_Z = 1 << 2,
    AGZ_ROTATE_X = 1 << 3,
    AGZ_ROTATE_Y = 1 << 4,
    AGZ_ROTATE_Z = 1 << 5,
    AGZ_ROTATE_SCREEN = 1 << 6,
    AGZ_SCALE_X = 1 << 7,
    AGZ_SCALE_Y = 1 << 8,
    AGZ_SCALE_Z = 1 << 9,
    AGZ_BOUNDS = 1 << 10,
    AGZ_SCALE_XU = 1 << 11,
    AGZ_SCALE_YU = 1 << 12,
    AGZ_SCALE_ZU = 1 << 13,
    AGZ_TRANSLATE = AGZ_TRANSLATE_X | AGZ_TRANSLATE_Y | AGZ_TRANSLATE_Z,
    AGZ_ROTATE = AGZ_ROTATE_X | AGZ_ROTATE_Y | AGZ_ROTATE_Z | AGZ_ROTATE_SCREEN,
    AGZ_SCALE = AGZ_SCALE_X | AGZ_SCALE_Y | AGZ_SCALE_Z,
    AGZ_SCALE_UNIFORM = AGZ_SCALE_XU | AGZ_SCALE_YU | AGZ_SCALE_ZU,
    AGZ_UNIVERSAL = AGZ_TRANSLATE | AGZ_ROTATE | AGZ_SCALE_UNIFORM,
} AgzOperation;

/** Whether the gizmo's axes follow the object (local) or the world. */
typedef enum AgzMode
{
    AGZ_LOCAL = 0,
    AGZ_WORLD = 1,
} AgzMode;

/** Starts ImGuizmo's frame: call it after the ImGui frame has begun, before any other call. */
APOGEE_IMGUIZMO_API void agz_begin_frame(void);

/** Draws into `drawList` (an ImDrawList*), or with NULL into the current window's. */
APOGEE_IMGUIZMO_API void agz_set_drawlist(void* drawList);

/** The rectangle of the viewport the gizmo is drawn over and takes the mouse in, in screen pixels. */
APOGEE_IMGUIZMO_API void agz_set_rect(float x, float y, float width, float height);

/** Whether the camera is orthographic (true) or perspective. */
APOGEE_IMGUIZMO_API void agz_set_orthographic(bool orthographic);

/** Enables or disables the gizmos (a disabled one is drawn and takes no input). */
APOGEE_IMGUIZMO_API void agz_enable(bool enable);

/** Separates gizmos that are drawn in the same window: the id stack of ImGui applies (push and pop an id around each). */
APOGEE_IMGUIZMO_API void agz_push_id_int(int id);
APOGEE_IMGUIZMO_API void agz_pop_id(void);

/** The size of the gizmo as a fraction of the clip space (0.1 by default). */
APOGEE_IMGUIZMO_API void agz_set_gizmo_size_clip_space(float size);

/** Whether an axis may flip to face the camera (true by default). */
APOGEE_IMGUIZMO_API void agz_allow_axis_flip(bool allow);

/**
 * Draws a gizmo for the object whose world matrix is `matrix` (16 floats, changed in place when the player drags it), seen with `view` and `projection`. `deltaMatrix` (NULL or 16 floats) receives
 * the change of this drag; `snap` (NULL or 3 floats) is the step of the operation (meters, degrees or scale in x, y, z); `localBounds` (6 floats: min and max) and `boundsSnap` (3) are for AGZ_BOUNDS.
 * Returns true while the matrix is being changed.
 */
APOGEE_IMGUIZMO_API bool agz_manipulate(const float* view, const float* projection, int32_t operation, int32_t mode, float* matrix, float* deltaMatrix, const float* snap, const float* localBounds, const float* boundsSnap);

/** A cube in a corner of the viewport that shows the camera's orientation and rotates `view` (16 floats, changed in place) when the player clicks or drags it. `length` is the distance to the pivot. */
APOGEE_IMGUIZMO_API void agz_view_manipulate(float* view, float length, float x, float y, float width, float height, uint32_t backgroundColor);

/** The pointer is over a gizmo's handle (any, or one operation), or the player is using it. */
APOGEE_IMGUIZMO_API bool agz_is_over(void);
APOGEE_IMGUIZMO_API bool agz_is_over_operation(int32_t operation);
APOGEE_IMGUIZMO_API bool agz_is_using(void);
APOGEE_IMGUIZMO_API bool agz_is_using_view_manipulate(void);
APOGEE_IMGUIZMO_API bool agz_is_view_manipulate_hovered(void);
/** The player is using any ImGuizmo widget. */
APOGEE_IMGUIZMO_API bool agz_is_using_any(void);

/** A grid of lines in the plane of `matrix` (the identity for the ground), `gridSize` meters to each side. */
APOGEE_IMGUIZMO_API void agz_draw_grid(const float* view, const float* projection, const float* matrix, float gridSize);

/** Draws `matrixCount` cubes (16 floats each) in `matrices`. */
APOGEE_IMGUIZMO_API void agz_draw_cubes(const float* view, const float* projection, const float* matrices, int32_t matrixCount);

/** A matrix's translation, rotation (Euler angles in degrees) and scale (3 floats each), and back. */
APOGEE_IMGUIZMO_API void agz_decompose_matrix(const float* matrix, float* translation, float* rotation, float* scale);
APOGEE_IMGUIZMO_API void agz_recompose_matrix(const float* translation, const float* rotation, const float* scale, float* matrix);

#ifdef __cplusplus
}
#endif

#endif
