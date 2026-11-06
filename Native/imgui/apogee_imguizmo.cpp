// Our C API over ImGuizmo: see apogee_imguizmo.h.

#include "apogee_imguizmo.h"

#include <imgui.h>

#include "ImGuizmo.h"

static_assert((int)ImGuizmo::TRANSLATE == (int)AGZ_TRANSLATE && (int)ImGuizmo::ROTATE == (int)AGZ_ROTATE && (int)ImGuizmo::SCALE == (int)AGZ_SCALE
    && (int)ImGuizmo::SCALEU == (int)AGZ_SCALE_UNIFORM && (int)ImGuizmo::UNIVERSAL == (int)AGZ_UNIVERSAL && (int)ImGuizmo::BOUNDS == (int)AGZ_BOUNDS, "ImGuizmo::OPERATION");
static_assert((int)ImGuizmo::LOCAL == (int)AGZ_LOCAL && (int)ImGuizmo::WORLD == (int)AGZ_WORLD, "ImGuizmo::MODE");

extern "C"
{
    APOGEE_IMGUIZMO_API void agz_begin_frame(void) { ImGuizmo::BeginFrame(); }

    APOGEE_IMGUIZMO_API void agz_set_drawlist(void* drawList) { ImGuizmo::SetDrawlist(static_cast<ImDrawList*>(drawList)); }

    APOGEE_IMGUIZMO_API void agz_set_rect(float x, float y, float width, float height) { ImGuizmo::SetRect(x, y, width, height); }

    APOGEE_IMGUIZMO_API void agz_set_orthographic(bool orthographic) { ImGuizmo::SetOrthographic(orthographic); }

    APOGEE_IMGUIZMO_API void agz_enable(bool enable) { ImGuizmo::Enable(enable); }

    APOGEE_IMGUIZMO_API void agz_push_id_int(int id) { ImGuizmo::PushID(id); }

    APOGEE_IMGUIZMO_API void agz_pop_id(void) { ImGuizmo::PopID(); }

    APOGEE_IMGUIZMO_API void agz_set_gizmo_size_clip_space(float size) { ImGuizmo::SetGizmoSizeClipSpace(size); }

    APOGEE_IMGUIZMO_API void agz_allow_axis_flip(bool allow) { ImGuizmo::AllowAxisFlip(allow); }

    APOGEE_IMGUIZMO_API bool agz_manipulate(const float* view, const float* projection, int32_t operation, int32_t mode, float* matrix, float* deltaMatrix, const float* snap, const float* localBounds, const float* boundsSnap)
    {
        return ImGuizmo::Manipulate(view, projection, (ImGuizmo::OPERATION)operation, (ImGuizmo::MODE)mode, matrix, deltaMatrix, snap, localBounds, boundsSnap);
    }

    APOGEE_IMGUIZMO_API void agz_view_manipulate(float* view, float length, float x, float y, float width, float height, uint32_t backgroundColor)
    {
        ImGuizmo::ViewManipulate(view, length, ImVec2(x, y), ImVec2(width, height), backgroundColor);
    }

    APOGEE_IMGUIZMO_API bool agz_is_over(void) { return ImGuizmo::IsOver(); }

    APOGEE_IMGUIZMO_API bool agz_is_over_operation(int32_t operation) { return ImGuizmo::IsOver((ImGuizmo::OPERATION)operation); }

    APOGEE_IMGUIZMO_API bool agz_is_using(void) { return ImGuizmo::IsUsing(); }

    APOGEE_IMGUIZMO_API bool agz_is_using_view_manipulate(void) { return ImGuizmo::IsUsingViewManipulate(); }

    APOGEE_IMGUIZMO_API bool agz_is_view_manipulate_hovered(void) { return ImGuizmo::IsViewManipulateHovered(); }

    APOGEE_IMGUIZMO_API bool agz_is_using_any(void) { return ImGuizmo::IsUsingAny(); }

    APOGEE_IMGUIZMO_API void agz_draw_grid(const float* view, const float* projection, const float* matrix, float gridSize) { ImGuizmo::DrawGrid(view, projection, matrix, gridSize); }

    APOGEE_IMGUIZMO_API void agz_draw_cubes(const float* view, const float* projection, const float* matrices, int32_t matrixCount) { ImGuizmo::DrawCubes(view, projection, matrices, matrixCount); }

    APOGEE_IMGUIZMO_API void agz_decompose_matrix(const float* matrix, float* translation, float* rotation, float* scale) { ImGuizmo::DecomposeMatrixToComponents(matrix, translation, rotation, scale); }

    APOGEE_IMGUIZMO_API void agz_recompose_matrix(const float* translation, const float* rotation, const float* scale, float* matrix) { ImGuizmo::RecomposeMatrixFromComponents(translation, rotation, scale, matrix); }
}
