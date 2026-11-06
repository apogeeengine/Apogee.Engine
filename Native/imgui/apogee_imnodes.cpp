// Our C API over imnodes: see apogee_imnodes.h.

#include "apogee_imnodes.h"

#include <imgui.h>

#include "imnodes.h"

static_assert((int)ImNodesPinShape_Circle == (int)ANODES_PIN_CIRCLE && (int)ImNodesPinShape_CircleFilled == (int)ANODES_PIN_CIRCLE_FILLED && (int)ImNodesPinShape_Triangle == (int)ANODES_PIN_TRIANGLE
    && (int)ImNodesPinShape_TriangleFilled == (int)ANODES_PIN_TRIANGLE_FILLED && (int)ImNodesPinShape_Quad == (int)ANODES_PIN_QUAD && (int)ImNodesPinShape_QuadFilled == (int)ANODES_PIN_QUAD_FILLED, "ImNodesPinShape");
static_assert((int)ImNodesMiniMapLocation_BottomLeft == (int)ANODES_MINIMAP_BOTTOM_LEFT && (int)ImNodesMiniMapLocation_BottomRight == (int)ANODES_MINIMAP_BOTTOM_RIGHT
    && (int)ImNodesMiniMapLocation_TopLeft == (int)ANODES_MINIMAP_TOP_LEFT && (int)ImNodesMiniMapLocation_TopRight == (int)ANODES_MINIMAP_TOP_RIGHT, "ImNodesMiniMapLocation");
static_assert((int)ImNodesCol_NodeBackground == (int)ANODES_COL_NODE_BACKGROUND && (int)ImNodesCol_NodeOutline == (int)ANODES_COL_NODE_OUTLINE && (int)ImNodesCol_TitleBar == (int)ANODES_COL_TITLE_BAR
    && (int)ImNodesCol_Link == (int)ANODES_COL_LINK && (int)ImNodesCol_Pin == (int)ANODES_COL_PIN && (int)ImNodesCol_BoxSelector == (int)ANODES_COL_BOX_SELECTOR && (int)ImNodesCol_GridBackground == (int)ANODES_COL_GRID_BACKGROUND
    && (int)ImNodesCol_GridLinePrimary == (int)ANODES_COL_GRID_LINE_PRIMARY, "ImNodesCol");

namespace
{
    void Write(const ImVec2& from, float* x, float* y)
    {
        if (x != nullptr)
            *x = from.x;
        if (y != nullptr)
            *y = from.y;
    }
}

extern "C"
{
    APOGEE_IMNODES_API AnodesContext* anodes_create_context(void) { return reinterpret_cast<AnodesContext*>(ImNodes::CreateContext()); }

    APOGEE_IMNODES_API void anodes_destroy_context(AnodesContext* context) { ImNodes::DestroyContext(reinterpret_cast<ImNodesContext*>(context)); }

    APOGEE_IMNODES_API void anodes_set_current_context(AnodesContext* context) { ImNodes::SetCurrentContext(reinterpret_cast<ImNodesContext*>(context)); }

    APOGEE_IMNODES_API AnodesEditor* anodes_editor_create(void) { return reinterpret_cast<AnodesEditor*>(ImNodes::EditorContextCreate()); }

    APOGEE_IMNODES_API void anodes_editor_free(AnodesEditor* editor) { ImNodes::EditorContextFree(reinterpret_cast<ImNodesEditorContext*>(editor)); }

    APOGEE_IMNODES_API void anodes_editor_set(AnodesEditor* editor) { ImNodes::EditorContextSet(reinterpret_cast<ImNodesEditorContext*>(editor)); }

    APOGEE_IMNODES_API void anodes_editor_reset_panning(float x, float y) { ImNodes::EditorContextResetPanning(ImVec2(x, y)); }

    APOGEE_IMNODES_API void anodes_style_colors_dark(void) { ImNodes::StyleColorsDark(); }

    APOGEE_IMNODES_API void anodes_style_colors_light(void) { ImNodes::StyleColorsLight(); }

    APOGEE_IMNODES_API void anodes_push_color_style(int32_t color, uint32_t abgr) { ImNodes::PushColorStyle(color, abgr); }

    APOGEE_IMNODES_API void anodes_pop_color_style(void) { ImNodes::PopColorStyle(); }

    APOGEE_IMNODES_API void anodes_begin_node_editor(void) { ImNodes::BeginNodeEditor(); }

    APOGEE_IMNODES_API void anodes_end_node_editor(void) { ImNodes::EndNodeEditor(); }

    APOGEE_IMNODES_API void anodes_minimap(float sizeFraction, int32_t location) { ImNodes::MiniMap(sizeFraction, location); }

    APOGEE_IMNODES_API void anodes_begin_node(int32_t id) { ImNodes::BeginNode(id); }

    APOGEE_IMNODES_API void anodes_end_node(void) { ImNodes::EndNode(); }

    APOGEE_IMNODES_API void anodes_begin_node_title_bar(void) { ImNodes::BeginNodeTitleBar(); }

    APOGEE_IMNODES_API void anodes_end_node_title_bar(void) { ImNodes::EndNodeTitleBar(); }

    APOGEE_IMNODES_API void anodes_begin_input_attribute(int32_t id, int32_t shape) { ImNodes::BeginInputAttribute(id, shape); }

    APOGEE_IMNODES_API void anodes_end_input_attribute(void) { ImNodes::EndInputAttribute(); }

    APOGEE_IMNODES_API void anodes_begin_output_attribute(int32_t id, int32_t shape) { ImNodes::BeginOutputAttribute(id, shape); }

    APOGEE_IMNODES_API void anodes_end_output_attribute(void) { ImNodes::EndOutputAttribute(); }

    APOGEE_IMNODES_API void anodes_begin_static_attribute(int32_t id) { ImNodes::BeginStaticAttribute(id); }

    APOGEE_IMNODES_API void anodes_end_static_attribute(void) { ImNodes::EndStaticAttribute(); }

    APOGEE_IMNODES_API void anodes_link(int32_t id, int32_t startAttribute, int32_t endAttribute) { ImNodes::Link(id, startAttribute, endAttribute); }

    APOGEE_IMNODES_API void anodes_set_node_editor_space_pos(int32_t node, float x, float y) { ImNodes::SetNodeEditorSpacePos(node, ImVec2(x, y)); }

    APOGEE_IMNODES_API void anodes_set_node_grid_space_pos(int32_t node, float x, float y) { ImNodes::SetNodeGridSpacePos(node, ImVec2(x, y)); }

    APOGEE_IMNODES_API void anodes_set_node_screen_space_pos(int32_t node, float x, float y) { ImNodes::SetNodeScreenSpacePos(node, ImVec2(x, y)); }

    APOGEE_IMNODES_API void anodes_get_node_editor_space_pos(int32_t node, float* x, float* y) { Write(ImNodes::GetNodeEditorSpacePos(node), x, y); }

    APOGEE_IMNODES_API void anodes_get_node_grid_space_pos(int32_t node, float* x, float* y) { Write(ImNodes::GetNodeGridSpacePos(node), x, y); }

    APOGEE_IMNODES_API void anodes_get_node_screen_space_pos(int32_t node, float* x, float* y) { Write(ImNodes::GetNodeScreenSpacePos(node), x, y); }

    APOGEE_IMNODES_API void anodes_get_node_dimensions(int32_t node, float* width, float* height) { Write(ImNodes::GetNodeDimensions(node), width, height); }

    APOGEE_IMNODES_API void anodes_set_node_draggable(int32_t node, bool draggable) { ImNodes::SetNodeDraggable(node, draggable); }

    APOGEE_IMNODES_API void anodes_snap_node_to_grid(int32_t node) { ImNodes::SnapNodeToGrid(node); }

    APOGEE_IMNODES_API bool anodes_is_editor_hovered(void) { return ImNodes::IsEditorHovered(); }

    APOGEE_IMNODES_API bool anodes_is_node_hovered(int32_t* node) { return ImNodes::IsNodeHovered(node); }

    APOGEE_IMNODES_API bool anodes_is_link_hovered(int32_t* link) { return ImNodes::IsLinkHovered(link); }

    APOGEE_IMNODES_API bool anodes_is_pin_hovered(int32_t* attribute) { return ImNodes::IsPinHovered(attribute); }

    APOGEE_IMNODES_API bool anodes_is_link_created(int32_t* startAttribute, int32_t* endAttribute) { return ImNodes::IsLinkCreated(startAttribute, endAttribute); }

    APOGEE_IMNODES_API bool anodes_is_link_destroyed(int32_t* link) { return ImNodes::IsLinkDestroyed(link); }

    APOGEE_IMNODES_API int32_t anodes_num_selected_nodes(void) { return ImNodes::NumSelectedNodes(); }

    APOGEE_IMNODES_API int32_t anodes_num_selected_links(void) { return ImNodes::NumSelectedLinks(); }

    APOGEE_IMNODES_API void anodes_get_selected_nodes(int32_t* ids) { ImNodes::GetSelectedNodes(ids); }

    APOGEE_IMNODES_API void anodes_get_selected_links(int32_t* ids) { ImNodes::GetSelectedLinks(ids); }

    APOGEE_IMNODES_API void anodes_clear_node_selection(void) { ImNodes::ClearNodeSelection(); }

    APOGEE_IMNODES_API void anodes_clear_link_selection(void) { ImNodes::ClearLinkSelection(); }

    APOGEE_IMNODES_API void anodes_select_node(int32_t node) { ImNodes::SelectNode(node); }

    APOGEE_IMNODES_API void anodes_select_link(int32_t link) { ImNodes::SelectLink(link); }

    APOGEE_IMNODES_API bool anodes_is_node_selected(int32_t node) { return ImNodes::IsNodeSelected(node); }

    APOGEE_IMNODES_API bool anodes_is_link_selected(int32_t link) { return ImNodes::IsLinkSelected(link); }
}
