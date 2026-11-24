using System;
using System.Numerics;
using Weft.Core.Numerics;

namespace Weft.Editor.Assets;

/// <summary>
/// The editor's screen as <see cref="DropFlow"/> sees it: the window rectangles the panels recorded when they were drawn, the folder the asset browser shows, whether a popup is open. The app fills it in
/// at the end of every frame, and the platform's drop events (pumped at the start of the next one) are routed with what the last frame drew, which is what the user was looking at when they let go. A panel
/// that was not drawn (collapsed, hidden behind another tab) has no rectangle and takes no drop.
/// </summary>
public sealed class PanelDropSurface : IDropSurface
{
    /// <summary>The viewport's window on the screen, in window points, or null when it was not drawn.</summary>
    public (Vector2 Min, Vector2 Size)? ViewportRect { get; set; }

    /// <summary>The asset browser's window, or null when it was not drawn.</summary>
    public (Vector2 Min, Vector2 Size)? AssetsRect { get; set; }

    /// <inheritdoc/>
    public string AssetFolder { get; set; } = "res://";

    /// <inheritdoc/>
    public bool ModalOpen { get; set; }

    /// <summary>Finds the point in the world a window point looks at (the viewport panel's ray), or null when it has no image.</summary>
    public Func<Vector2, Double3?>? Placer { get; set; }

    /// <inheritdoc/>
    public DropArea AreaAt(float x, float y)
    {
        var point = new Vector2(x, y);
        if (Contains(ViewportRect, point))
        {
            return DropArea.Viewport;
        }

        return Contains(AssetsRect, point) ? DropArea.AssetBrowser : DropArea.None;
    }

    /// <inheritdoc/>
    public Double3? PlacementAt(float x, float y) => Placer?.Invoke(new Vector2(x, y));

    private static bool Contains((Vector2 Min, Vector2 Size)? rect, Vector2 point) =>
        rect is { } r && point.X >= r.Min.X && point.Y >= r.Min.Y && point.X < r.Min.X + r.Size.X && point.Y < r.Min.Y + r.Size.Y;
}
