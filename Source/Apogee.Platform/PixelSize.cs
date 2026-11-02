namespace Apogee.Platform;

/// <summary>A size in pixels: what a swapchain is created with (on a high-density display it is larger than the window's size in points).</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"{Width}x{Height}";
}
