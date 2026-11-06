/*
 * Our C API over imnodes (ThirdParty/imnodes): a node editor drawn with Dear ImGui (nodes with titles and pins, links, selection, a minimap), for the editor's render graph view and the graph editors
 * to come. imnodes is C++; this is plain functions over it, in the same shared library as Dear ImGui (cimgui), acting on ImGui's current context: make it current, create an imnodes context
 * (anodes_create_context) and make that current too.
 *
 * Ids are the caller's integers: nodes, pins (attributes) and links each have their own (an attribute id is unique among attributes, a link's among links). The calls nest like imnodes':
 *   anodes_begin_node_editor
 *     anodes_begin_node(id)
 *       anodes_begin_node_title_bar ... anodes_end_node_title_bar    (ImGui widgets inside draw the title)
 *       anodes_begin_input_attribute(id, shape) ... anodes_end_input_attribute
 *       anodes_begin_output_attribute(id, shape) ... anodes_end_output_attribute
 *     anodes_end_node
 *     anodes_link(id, startAttribute, endAttribute)
 *     anodes_minimap(...)
 *   anodes_end_node_editor
 * Questions about what the player did (links made, selection) are asked after anodes_end_node_editor. None of it throws.
 */
#ifndef APOGEE_IMNODES_H
#define APOGEE_IMNODES_H

#include <stdbool.h>
#include <stdint.h>

#ifndef APOGEE_IMNODES_API
#define APOGEE_IMNODES_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct AnodesContext AnodesContext;
typedef struct AnodesEditor AnodesEditor;

/** The shape of a pin (ImNodesPinShape). */
typedef enum AnodesPinShape
{
    ANODES_PIN_CIRCLE = 0,
    ANODES_PIN_CIRCLE_FILLED = 1,
    ANODES_PIN_TRIANGLE = 2,
    ANODES_PIN_TRIANGLE_FILLED = 3,
    ANODES_PIN_QUAD = 4,
    ANODES_PIN_QUAD_FILLED = 5,
} AnodesPinShape;

/** Where the minimap goes (ImNodesMiniMapLocation). */
typedef enum AnodesMiniMapLocation
{
    ANODES_MINIMAP_BOTTOM_LEFT = 0,
    ANODES_MINIMAP_BOTTOM_RIGHT = 1,
    ANODES_MINIMAP_TOP_LEFT = 2,
    ANODES_MINIMAP_TOP_RIGHT = 3,
} AnodesMiniMapLocation;

/** The colors of the editor (ImNodesCol) that anodes_push_color_style takes; ABGR like ImGui's ImU32. */
typedef enum AnodesColor
{
    ANODES_COL_NODE_BACKGROUND = 0,
    ANODES_COL_NODE_BACKGROUND_HOVERED = 1,
    ANODES_COL_NODE_BACKGROUND_SELECTED = 2,
    ANODES_COL_NODE_OUTLINE = 3,
    ANODES_COL_TITLE_BAR = 4,
    ANODES_COL_TITLE_BAR_HOVERED = 5,
    ANODES_COL_TITLE_BAR_SELECTED = 6,
    ANODES_COL_LINK = 7,
    ANODES_COL_LINK_HOVERED = 8,
    ANODES_COL_LINK_SELECTED = 9,
    ANODES_COL_PIN = 10,
    ANODES_COL_PIN_HOVERED = 11,
    ANODES_COL_BOX_SELECTOR = 12,
    ANODES_COL_BOX_SELECTOR_OUTLINE = 13,
    ANODES_COL_GRID_BACKGROUND = 14,
    ANODES_COL_GRID_LINE = 15,
    ANODES_COL_GRID_LINE_PRIMARY = 16,
} AnodesColor;

/* ---- Contexts ---- */

/** Creates an imnodes context (ImGui's context must exist and be current) and makes it current. */
APOGEE_IMNODES_API AnodesContext* anodes_create_context(void);
/** Destroys a context (NULL: the current one). */
APOGEE_IMNODES_API void anodes_destroy_context(AnodesContext* context);
APOGEE_IMNODES_API void anodes_set_current_context(AnodesContext* context);

/** An editor holds the panning, the node positions and the selection of one node editor; the default context has one. More are for several editors in one frame. */
APOGEE_IMNODES_API AnodesEditor* anodes_editor_create(void);
APOGEE_IMNODES_API void anodes_editor_free(AnodesEditor* editor);
APOGEE_IMNODES_API void anodes_editor_set(AnodesEditor* editor);
APOGEE_IMNODES_API void anodes_editor_reset_panning(float x, float y);

APOGEE_IMNODES_API void anodes_style_colors_dark(void);
APOGEE_IMNODES_API void anodes_style_colors_light(void);
APOGEE_IMNODES_API void anodes_push_color_style(int32_t color, uint32_t abgr);
APOGEE_IMNODES_API void anodes_pop_color_style(void);

/* ---- The editor and its nodes ---- */

APOGEE_IMNODES_API void anodes_begin_node_editor(void);
APOGEE_IMNODES_API void anodes_end_node_editor(void);
APOGEE_IMNODES_API void anodes_minimap(float sizeFraction, int32_t location);

APOGEE_IMNODES_API void anodes_begin_node(int32_t id);
APOGEE_IMNODES_API void anodes_end_node(void);
APOGEE_IMNODES_API void anodes_begin_node_title_bar(void);
APOGEE_IMNODES_API void anodes_end_node_title_bar(void);
APOGEE_IMNODES_API void anodes_begin_input_attribute(int32_t id, int32_t shape);
APOGEE_IMNODES_API void anodes_end_input_attribute(void);
APOGEE_IMNODES_API void anodes_begin_output_attribute(int32_t id, int32_t shape);
APOGEE_IMNODES_API void anodes_end_output_attribute(void);
/** A line of the node that is no pin (an ImGui widget the node holds). */
APOGEE_IMNODES_API void anodes_begin_static_attribute(int32_t id);
APOGEE_IMNODES_API void anodes_end_static_attribute(void);
APOGEE_IMNODES_API void anodes_link(int32_t id, int32_t startAttribute, int32_t endAttribute);

/* ---- Positions: in the editor's space (panning applied), the grid's space (what is saved) or the screen's. A node that has no position yet is placed when it is first drawn. ---- */

APOGEE_IMNODES_API void anodes_set_node_editor_space_pos(int32_t node, float x, float y);
APOGEE_IMNODES_API void anodes_set_node_grid_space_pos(int32_t node, float x, float y);
APOGEE_IMNODES_API void anodes_set_node_screen_space_pos(int32_t node, float x, float y);
APOGEE_IMNODES_API void anodes_get_node_editor_space_pos(int32_t node, float* x, float* y);
APOGEE_IMNODES_API void anodes_get_node_grid_space_pos(int32_t node, float* x, float* y);
APOGEE_IMNODES_API void anodes_get_node_screen_space_pos(int32_t node, float* x, float* y);
APOGEE_IMNODES_API void anodes_get_node_dimensions(int32_t node, float* width, float* height);
APOGEE_IMNODES_API void anodes_set_node_draggable(int32_t node, bool draggable);
APOGEE_IMNODES_API void anodes_snap_node_to_grid(int32_t node);

/* ---- What the player did, after anodes_end_node_editor ---- */

APOGEE_IMNODES_API bool anodes_is_editor_hovered(void);
APOGEE_IMNODES_API bool anodes_is_node_hovered(int32_t* node);
APOGEE_IMNODES_API bool anodes_is_link_hovered(int32_t* link);
APOGEE_IMNODES_API bool anodes_is_pin_hovered(int32_t* attribute);
/** A link was made by dragging from one pin to another: the two attributes. */
APOGEE_IMNODES_API bool anodes_is_link_created(int32_t* startAttribute, int32_t* endAttribute);
/** A link was detached from a pin: its id. */
APOGEE_IMNODES_API bool anodes_is_link_destroyed(int32_t* link);

APOGEE_IMNODES_API int32_t anodes_num_selected_nodes(void);
APOGEE_IMNODES_API int32_t anodes_num_selected_links(void);
/** Writes the selected nodes' ids (or links') into `ids`, which has room for anodes_num_selected_* of them. */
APOGEE_IMNODES_API void anodes_get_selected_nodes(int32_t* ids);
APOGEE_IMNODES_API void anodes_get_selected_links(int32_t* ids);
APOGEE_IMNODES_API void anodes_clear_node_selection(void);
APOGEE_IMNODES_API void anodes_clear_link_selection(void);
APOGEE_IMNODES_API void anodes_select_node(int32_t node);
APOGEE_IMNODES_API void anodes_select_link(int32_t link);
APOGEE_IMNODES_API bool anodes_is_node_selected(int32_t node);
APOGEE_IMNODES_API bool anodes_is_link_selected(int32_t link);

#ifdef __cplusplus
}
#endif

#endif
