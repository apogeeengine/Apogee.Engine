/*
 * Our C API over ImPlot (ThirdParty/implot): plots (lines, shaded areas, bars, scatter) drawn with Dear ImGui, for the dev overlay's graphs and the editor's profiler. ImPlot is C++ with templates over
 * the element type; this is plain functions with float (`_f`) and double (`_d`) versions. It lives in the same shared library as Dear ImGui (cimgui) and acts on ImGui's current context: make it
 * current, create an ImPlot context (aplot_create_context) and make that current too.
 *
 * Use: aplot_begin_plot, the aplot_setup_* calls, then plot items, then aplot_end_plot (only when begin_plot returned true). Item data is plain arrays; a plot item's look is an AplotSpec
 * (NULL: ImPlot's default, which cycles the colormap). None of it throws.
 */
#ifndef APOGEE_IMPLOT_H
#define APOGEE_IMPLOT_H

#include <stdbool.h>
#include <stdint.h>

#ifndef APOGEE_IMPLOT_API
#define APOGEE_IMPLOT_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct AplotContext AplotContext;

/** The axes of a plot (ImAxis): x1 and y1 exist by default. */
typedef enum AplotAxis
{
    APLOT_AXIS_X1 = 0,
    APLOT_AXIS_X2 = 1,
    APLOT_AXIS_X3 = 2,
    APLOT_AXIS_Y1 = 3,
    APLOT_AXIS_Y2 = 4,
    APLOT_AXIS_Y3 = 5,
} AplotAxis;

/** Options of a plot (ImPlotFlags). */
typedef enum AplotFlags
{
    APLOT_NO_TITLE = 1 << 0,
    APLOT_NO_LEGEND = 1 << 1,
    APLOT_NO_MOUSE_TEXT = 1 << 2,
    APLOT_NO_INPUTS = 1 << 3,
    APLOT_NO_MENUS = 1 << 4,
    APLOT_NO_BOX_SELECT = 1 << 5,
    APLOT_NO_FRAME = 1 << 6,
    APLOT_EQUAL = 1 << 7,
    APLOT_CROSSHAIRS = 1 << 8,
    APLOT_CANVAS_ONLY = APLOT_NO_TITLE | APLOT_NO_LEGEND | APLOT_NO_MENUS | APLOT_NO_BOX_SELECT | APLOT_NO_MOUSE_TEXT,
} AplotFlags;

/** Options of an axis (ImPlotAxisFlags). */
typedef enum AplotAxisFlags
{
    APLOT_AXIS_NO_LABEL = 1 << 0,
    APLOT_AXIS_NO_GRID_LINES = 1 << 1,
    APLOT_AXIS_NO_TICK_MARKS = 1 << 2,
    APLOT_AXIS_NO_TICK_LABELS = 1 << 3,
    APLOT_AXIS_NO_INITIAL_FIT = 1 << 4,
    APLOT_AXIS_NO_MENUS = 1 << 5,
    APLOT_AXIS_NO_SIDE_SWITCH = 1 << 6,
    APLOT_AXIS_NO_HIGHLIGHT = 1 << 7,
    APLOT_AXIS_OPPOSITE = 1 << 8,
    APLOT_AXIS_FOREGROUND = 1 << 9,
    APLOT_AXIS_INVERT = 1 << 10,
    APLOT_AXIS_AUTO_FIT = 1 << 11,
    APLOT_AXIS_RANGE_FIT = 1 << 12,
    APLOT_AXIS_PAN_STRETCH = 1 << 13,
    APLOT_AXIS_LOCK_MIN = 1 << 14,
    APLOT_AXIS_LOCK_MAX = 1 << 15,
    APLOT_AXIS_LOCK = APLOT_AXIS_LOCK_MIN | APLOT_AXIS_LOCK_MAX,
    APLOT_AXIS_NO_DECORATIONS = APLOT_AXIS_NO_LABEL | APLOT_AXIS_NO_GRID_LINES | APLOT_AXIS_NO_TICK_MARKS | APLOT_AXIS_NO_TICK_LABELS,
} AplotAxisFlags;

/** Where the legend goes (ImPlotLocation). */
typedef enum AplotLocation
{
    APLOT_CENTER = 0,
    APLOT_NORTH = 1 << 0,
    APLOT_SOUTH = 1 << 1,
    APLOT_WEST = 1 << 2,
    APLOT_EAST = 1 << 3,
    APLOT_NORTH_WEST = APLOT_NORTH | APLOT_WEST,
    APLOT_NORTH_EAST = APLOT_NORTH | APLOT_EAST,
    APLOT_SOUTH_WEST = APLOT_SOUTH | APLOT_WEST,
    APLOT_SOUTH_EAST = APLOT_SOUTH | APLOT_EAST,
} AplotLocation;

/** Options of the legend (ImPlotLegendFlags). */
typedef enum AplotLegendFlags
{
    APLOT_LEGEND_NO_BUTTONS = 1 << 0,
    APLOT_LEGEND_NO_HIGHLIGHT_ITEM = 1 << 1,
    APLOT_LEGEND_NO_MENUS = 1 << 3,
    APLOT_LEGEND_OUTSIDE = 1 << 4,
    APLOT_LEGEND_HORIZONTAL = 1 << 5,
    APLOT_LEGEND_SORT = 1 << 6,
    APLOT_LEGEND_REVERSE = 1 << 7,
} AplotLegendFlags;

/** When a limit is applied (ImPlotCond, the values of ImGuiCond): always (the player cannot change it), or once. */
typedef enum AplotCond
{
    APLOT_COND_ALWAYS = 1,
    APLOT_COND_ONCE = 2,
} AplotCond;

/** A marker drawn at each point (ImPlotMarker). */
typedef enum AplotMarker
{
    APLOT_MARKER_NONE = -2,
    APLOT_MARKER_AUTO = -1,
    APLOT_MARKER_CIRCLE = 0,
    APLOT_MARKER_SQUARE = 1,
    APLOT_MARKER_DIAMOND = 2,
} AplotMarker;

/** Item flags that go in AplotSpec.flags (ImPlotItemFlags and the flags of the item's kind). */
typedef enum AplotItemFlags
{
    APLOT_ITEM_NO_LEGEND = 1 << 0,
    APLOT_ITEM_NO_FIT = 1 << 1,
    /** For a bar plot: horizontal bars. */
    APLOT_BARS_HORIZONTAL = 1 << 10,
    /** For a line plot: fill down to the origin. */
    APLOT_LINE_SHADED = 1 << 14,
} AplotItemFlags;

/**
 * How an item looks. A color with alpha below 0 is automatic: the next color of the colormap. Fill the struct with aplot_default_spec first, then change what you want.
 */
typedef struct AplotSpec
{
    float lineColor[4];
    float lineWeight;
    float fillColor[4];
    float fillAlpha;
    int32_t marker;
    float markerSize;
    int32_t flags;
} AplotSpec;

/** Fills `spec` with ImPlot's defaults (automatic colors, 1 pixel lines, no marker). */
APOGEE_IMPLOT_API void aplot_default_spec(AplotSpec* spec);

/* ---- Context ---- */

/** Creates an ImPlot context (ImGui's context must exist and be current) and makes it current. */
APOGEE_IMPLOT_API AplotContext* aplot_create_context(void);
/** Destroys a context (NULL: the current one). */
APOGEE_IMPLOT_API void aplot_destroy_context(AplotContext* context);
APOGEE_IMPLOT_API void aplot_set_current_context(AplotContext* context);

/* ---- A plot ---- */

/** Begins a plot of `width` x `height` pixels (0 or negative: what is left of the window); `title` is its id and its heading. Call aplot_end_plot if it returns true. */
APOGEE_IMPLOT_API bool aplot_begin_plot(const char* title, float width, float height, int32_t flags);
APOGEE_IMPLOT_API void aplot_end_plot(void);

/** The labels of the two main axes (NULL: none) and their options. */
APOGEE_IMPLOT_API void aplot_setup_axes(const char* xLabel, const char* yLabel, int32_t xFlags, int32_t yFlags);
APOGEE_IMPLOT_API void aplot_setup_axis(int32_t axis, const char* label, int32_t flags);
APOGEE_IMPLOT_API void aplot_setup_axis_limits(int32_t axis, double min, double max, int32_t cond);
APOGEE_IMPLOT_API void aplot_setup_axes_limits(double xMin, double xMax, double yMin, double yMax, int32_t cond);
/** A printf format for the tick labels of an axis ("%.1f ms"). */
APOGEE_IMPLOT_API void aplot_setup_axis_format(int32_t axis, const char* format);
APOGEE_IMPLOT_API void aplot_setup_legend(int32_t location, int32_t flags);
/** Ends the setup early (the first item does it otherwise). */
APOGEE_IMPLOT_API void aplot_setup_finish(void);
/** The next plot's axes fit the data on the first frame (every frame with APLOT_AXIS_AUTO_FIT). */
APOGEE_IMPLOT_API void aplot_set_next_axes_to_fit(void);

/* ---- Items: `values` are y values at x = xStart + i * xScale; the `xy` forms take x and y arrays. ---- */

APOGEE_IMPLOT_API void aplot_plot_line_f(const char* label, const float* values, int32_t count, double xScale, double xStart, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_line_d(const char* label, const double* values, int32_t count, double xScale, double xStart, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_line_xy_f(const char* label, const float* xs, const float* ys, int32_t count, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_line_xy_d(const char* label, const double* xs, const double* ys, int32_t count, const AplotSpec* spec);

/** A filled area between the line and `yRef`. */
APOGEE_IMPLOT_API void aplot_plot_shaded_f(const char* label, const float* values, int32_t count, double yRef, double xScale, double xStart, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_shaded_d(const char* label, const double* values, int32_t count, double yRef, double xScale, double xStart, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_shaded_xy_f(const char* label, const float* xs, const float* ys, int32_t count, double yRef, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_shaded_xy_d(const char* label, const double* xs, const double* ys, int32_t count, double yRef, const AplotSpec* spec);
/** A filled area between two lines. */
APOGEE_IMPLOT_API void aplot_plot_shaded_between_f(const char* label, const float* xs, const float* ys1, const float* ys2, int32_t count, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_shaded_between_d(const char* label, const double* xs, const double* ys1, const double* ys2, int32_t count, const AplotSpec* spec);

APOGEE_IMPLOT_API void aplot_plot_bars_f(const char* label, const float* values, int32_t count, double barSize, double shift, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_bars_d(const char* label, const double* values, int32_t count, double barSize, double shift, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_bars_xy_f(const char* label, const float* xs, const float* ys, int32_t count, double barSize, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_bars_xy_d(const char* label, const double* xs, const double* ys, int32_t count, double barSize, const AplotSpec* spec);

APOGEE_IMPLOT_API void aplot_plot_scatter_f(const char* label, const float* values, int32_t count, double xScale, double xStart, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_scatter_d(const char* label, const double* values, int32_t count, double xScale, double xStart, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_scatter_xy_f(const char* label, const float* xs, const float* ys, int32_t count, const AplotSpec* spec);
APOGEE_IMPLOT_API void aplot_plot_scatter_xy_d(const char* label, const double* xs, const double* ys, int32_t count, const AplotSpec* spec);

/** Whether the pointer is over the plot's area, and where, in plot units of the x1 and y1 axes (false: `x` and `y` are untouched). */
APOGEE_IMPLOT_API bool aplot_get_plot_mouse(double* x, double* y);

#ifdef __cplusplus
}
#endif

#endif
