// Our C API over ImPlot: see apogee_implot.h.

#include "apogee_implot.h"

#include <imgui.h>

#include "implot.h"

static_assert((int)ImAxis_X1 == (int)APLOT_AXIS_X1 && (int)ImAxis_X2 == (int)APLOT_AXIS_X2 && (int)ImAxis_X3 == (int)APLOT_AXIS_X3 && (int)ImAxis_Y1 == (int)APLOT_AXIS_Y1 && (int)ImAxis_Y2 == (int)APLOT_AXIS_Y2 && (int)ImAxis_Y3 == (int)APLOT_AXIS_Y3, "ImAxis");
static_assert((int)ImPlotFlags_NoTitle == (int)APLOT_NO_TITLE && (int)ImPlotFlags_NoLegend == (int)APLOT_NO_LEGEND && (int)ImPlotFlags_NoMouseText == (int)APLOT_NO_MOUSE_TEXT && (int)ImPlotFlags_NoInputs == (int)APLOT_NO_INPUTS
    && (int)ImPlotFlags_NoMenus == (int)APLOT_NO_MENUS && (int)ImPlotFlags_NoBoxSelect == (int)APLOT_NO_BOX_SELECT && (int)ImPlotFlags_NoFrame == (int)APLOT_NO_FRAME && (int)ImPlotFlags_Equal == (int)APLOT_EQUAL
    && (int)ImPlotFlags_Crosshairs == (int)APLOT_CROSSHAIRS && (int)ImPlotFlags_CanvasOnly == (int)APLOT_CANVAS_ONLY, "ImPlotFlags");
static_assert((int)ImPlotAxisFlags_NoLabel == (int)APLOT_AXIS_NO_LABEL && (int)ImPlotAxisFlags_NoGridLines == (int)APLOT_AXIS_NO_GRID_LINES && (int)ImPlotAxisFlags_NoTickMarks == (int)APLOT_AXIS_NO_TICK_MARKS
    && (int)ImPlotAxisFlags_NoTickLabels == (int)APLOT_AXIS_NO_TICK_LABELS && (int)ImPlotAxisFlags_NoInitialFit == (int)APLOT_AXIS_NO_INITIAL_FIT && (int)ImPlotAxisFlags_NoMenus == (int)APLOT_AXIS_NO_MENUS
    && (int)ImPlotAxisFlags_NoSideSwitch == (int)APLOT_AXIS_NO_SIDE_SWITCH && (int)ImPlotAxisFlags_NoHighlight == (int)APLOT_AXIS_NO_HIGHLIGHT && (int)ImPlotAxisFlags_Opposite == (int)APLOT_AXIS_OPPOSITE
    && (int)ImPlotAxisFlags_Foreground == (int)APLOT_AXIS_FOREGROUND && (int)ImPlotAxisFlags_Invert == (int)APLOT_AXIS_INVERT && (int)ImPlotAxisFlags_AutoFit == (int)APLOT_AXIS_AUTO_FIT
    && (int)ImPlotAxisFlags_RangeFit == (int)APLOT_AXIS_RANGE_FIT && (int)ImPlotAxisFlags_PanStretch == (int)APLOT_AXIS_PAN_STRETCH && (int)ImPlotAxisFlags_LockMin == (int)APLOT_AXIS_LOCK_MIN
    && (int)ImPlotAxisFlags_LockMax == (int)APLOT_AXIS_LOCK_MAX, "ImPlotAxisFlags");
static_assert((int)ImPlotLocation_Center == (int)APLOT_CENTER && (int)ImPlotLocation_North == (int)APLOT_NORTH && (int)ImPlotLocation_South == (int)APLOT_SOUTH && (int)ImPlotLocation_West == (int)APLOT_WEST
    && (int)ImPlotLocation_East == (int)APLOT_EAST && (int)ImPlotLocation_NorthWest == (int)APLOT_NORTH_WEST && (int)ImPlotLocation_SouthEast == (int)APLOT_SOUTH_EAST, "ImPlotLocation");
static_assert((int)ImPlotLegendFlags_NoButtons == (int)APLOT_LEGEND_NO_BUTTONS && (int)ImPlotLegendFlags_Outside == (int)APLOT_LEGEND_OUTSIDE && (int)ImPlotLegendFlags_Horizontal == (int)APLOT_LEGEND_HORIZONTAL
    && (int)ImPlotLegendFlags_Reverse == (int)APLOT_LEGEND_REVERSE, "ImPlotLegendFlags");
static_assert((int)ImPlotCond_Always == (int)APLOT_COND_ALWAYS && (int)ImPlotCond_Once == (int)APLOT_COND_ONCE, "ImPlotCond");
static_assert((int)ImPlotMarker_None == (int)APLOT_MARKER_NONE && (int)ImPlotMarker_Auto == (int)APLOT_MARKER_AUTO && (int)ImPlotMarker_Circle == (int)APLOT_MARKER_CIRCLE && (int)ImPlotMarker_Diamond == (int)APLOT_MARKER_DIAMOND, "ImPlotMarker");
static_assert((int)ImPlotItemFlags_NoLegend == (int)APLOT_ITEM_NO_LEGEND && (int)ImPlotItemFlags_NoFit == (int)APLOT_ITEM_NO_FIT && (int)ImPlotBarsFlags_Horizontal == (int)APLOT_BARS_HORIZONTAL
    && (int)ImPlotLineFlags_Shaded == (int)APLOT_LINE_SHADED, "ImPlotItemFlags");

namespace
{
    ImPlotSpec ToSpec(const AplotSpec* from)
    {
        ImPlotSpec spec;
        if (from == nullptr)
            return spec;
        spec.LineColor = ImVec4(from->lineColor[0], from->lineColor[1], from->lineColor[2], from->lineColor[3]);
        spec.LineWeight = from->lineWeight;
        spec.FillColor = ImVec4(from->fillColor[0], from->fillColor[1], from->fillColor[2], from->fillColor[3]);
        spec.FillAlpha = from->fillAlpha;
        spec.Marker = from->marker;
        spec.MarkerSize = from->markerSize;
        spec.Flags = from->flags;
        return spec;
    }
}

extern "C"
{
    APOGEE_IMPLOT_API void aplot_default_spec(AplotSpec* spec)
    {
        if (spec == nullptr)
            return;
        ImPlotSpec defaults;
        spec->lineColor[0] = defaults.LineColor.x; spec->lineColor[1] = defaults.LineColor.y; spec->lineColor[2] = defaults.LineColor.z; spec->lineColor[3] = defaults.LineColor.w;
        spec->lineWeight = defaults.LineWeight;
        spec->fillColor[0] = defaults.FillColor.x; spec->fillColor[1] = defaults.FillColor.y; spec->fillColor[2] = defaults.FillColor.z; spec->fillColor[3] = defaults.FillColor.w;
        spec->fillAlpha = defaults.FillAlpha;
        spec->marker = defaults.Marker;
        spec->markerSize = defaults.MarkerSize;
        spec->flags = defaults.Flags;
    }

    APOGEE_IMPLOT_API AplotContext* aplot_create_context(void) { return reinterpret_cast<AplotContext*>(ImPlot::CreateContext()); }

    APOGEE_IMPLOT_API void aplot_destroy_context(AplotContext* context) { ImPlot::DestroyContext(reinterpret_cast<ImPlotContext*>(context)); }

    APOGEE_IMPLOT_API void aplot_set_current_context(AplotContext* context) { ImPlot::SetCurrentContext(reinterpret_cast<ImPlotContext*>(context)); }

    APOGEE_IMPLOT_API bool aplot_begin_plot(const char* title, float width, float height, int32_t flags) { return ImPlot::BeginPlot(title, ImVec2(width, height), flags); }

    APOGEE_IMPLOT_API void aplot_end_plot(void) { ImPlot::EndPlot(); }

    APOGEE_IMPLOT_API void aplot_setup_axes(const char* xLabel, const char* yLabel, int32_t xFlags, int32_t yFlags) { ImPlot::SetupAxes(xLabel, yLabel, xFlags, yFlags); }

    APOGEE_IMPLOT_API void aplot_setup_axis(int32_t axis, const char* label, int32_t flags) { ImPlot::SetupAxis(axis, label, flags); }

    APOGEE_IMPLOT_API void aplot_setup_axis_limits(int32_t axis, double min, double max, int32_t cond) { ImPlot::SetupAxisLimits(axis, min, max, cond); }

    APOGEE_IMPLOT_API void aplot_setup_axes_limits(double xMin, double xMax, double yMin, double yMax, int32_t cond) { ImPlot::SetupAxesLimits(xMin, xMax, yMin, yMax, cond); }

    APOGEE_IMPLOT_API void aplot_setup_axis_format(int32_t axis, const char* format) { ImPlot::SetupAxisFormat(axis, format); }

    APOGEE_IMPLOT_API void aplot_setup_legend(int32_t location, int32_t flags) { ImPlot::SetupLegend(location, flags); }

    APOGEE_IMPLOT_API void aplot_setup_finish(void) { ImPlot::SetupFinish(); }

    APOGEE_IMPLOT_API void aplot_set_next_axes_to_fit(void) { ImPlot::SetNextAxesToFit(); }

    APOGEE_IMPLOT_API void aplot_plot_line_f(const char* label, const float* values, int32_t count, double xScale, double xStart, const AplotSpec* spec) { ImPlot::PlotLine(label, values, count, xScale, xStart, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_line_d(const char* label, const double* values, int32_t count, double xScale, double xStart, const AplotSpec* spec) { ImPlot::PlotLine(label, values, count, xScale, xStart, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_line_xy_f(const char* label, const float* xs, const float* ys, int32_t count, const AplotSpec* spec) { ImPlot::PlotLine(label, xs, ys, count, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_line_xy_d(const char* label, const double* xs, const double* ys, int32_t count, const AplotSpec* spec) { ImPlot::PlotLine(label, xs, ys, count, ToSpec(spec)); }

    APOGEE_IMPLOT_API void aplot_plot_shaded_f(const char* label, const float* values, int32_t count, double yRef, double xScale, double xStart, const AplotSpec* spec) { ImPlot::PlotShaded(label, values, count, yRef, xScale, xStart, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_shaded_d(const char* label, const double* values, int32_t count, double yRef, double xScale, double xStart, const AplotSpec* spec) { ImPlot::PlotShaded(label, values, count, yRef, xScale, xStart, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_shaded_xy_f(const char* label, const float* xs, const float* ys, int32_t count, double yRef, const AplotSpec* spec) { ImPlot::PlotShaded(label, xs, ys, count, yRef, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_shaded_xy_d(const char* label, const double* xs, const double* ys, int32_t count, double yRef, const AplotSpec* spec) { ImPlot::PlotShaded(label, xs, ys, count, yRef, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_shaded_between_f(const char* label, const float* xs, const float* ys1, const float* ys2, int32_t count, const AplotSpec* spec) { ImPlot::PlotShaded(label, xs, ys1, ys2, count, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_shaded_between_d(const char* label, const double* xs, const double* ys1, const double* ys2, int32_t count, const AplotSpec* spec) { ImPlot::PlotShaded(label, xs, ys1, ys2, count, ToSpec(spec)); }

    APOGEE_IMPLOT_API void aplot_plot_bars_f(const char* label, const float* values, int32_t count, double barSize, double shift, const AplotSpec* spec) { ImPlot::PlotBars(label, values, count, barSize, shift, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_bars_d(const char* label, const double* values, int32_t count, double barSize, double shift, const AplotSpec* spec) { ImPlot::PlotBars(label, values, count, barSize, shift, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_bars_xy_f(const char* label, const float* xs, const float* ys, int32_t count, double barSize, const AplotSpec* spec) { ImPlot::PlotBars(label, xs, ys, count, barSize, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_bars_xy_d(const char* label, const double* xs, const double* ys, int32_t count, double barSize, const AplotSpec* spec) { ImPlot::PlotBars(label, xs, ys, count, barSize, ToSpec(spec)); }

    APOGEE_IMPLOT_API void aplot_plot_scatter_f(const char* label, const float* values, int32_t count, double xScale, double xStart, const AplotSpec* spec) { ImPlot::PlotScatter(label, values, count, xScale, xStart, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_scatter_d(const char* label, const double* values, int32_t count, double xScale, double xStart, const AplotSpec* spec) { ImPlot::PlotScatter(label, values, count, xScale, xStart, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_scatter_xy_f(const char* label, const float* xs, const float* ys, int32_t count, const AplotSpec* spec) { ImPlot::PlotScatter(label, xs, ys, count, ToSpec(spec)); }
    APOGEE_IMPLOT_API void aplot_plot_scatter_xy_d(const char* label, const double* xs, const double* ys, int32_t count, const AplotSpec* spec) { ImPlot::PlotScatter(label, xs, ys, count, ToSpec(spec)); }

    APOGEE_IMPLOT_API bool aplot_get_plot_mouse(double* x, double* y)
    {
        if (!ImPlot::IsPlotHovered())
            return false;
        ImPlotPoint point = ImPlot::GetPlotMousePos();
        if (x != nullptr)
            *x = point.x;
        if (y != nullptr)
            *y = point.y;
        return true;
    }
}
