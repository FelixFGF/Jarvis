using System.Drawing;

namespace Jarvis.Services;

/// <summary>Maps pixels in a resized full-desktop screenshot back to physical Windows screen coordinates.</summary>
public readonly record struct ScreenshotCoordinateMap(Rectangle DesktopBounds, int ImageWidth, int ImageHeight)
{
    public bool IsValid => DesktopBounds.Width > 0 && DesktopBounds.Height > 0 && ImageWidth > 0 && ImageHeight > 0;

    public (int X, int Y) ToDesktop(int imageX, int imageY)
    {
        if (!IsValid) throw new InvalidOperationException("The screenshot coordinate mapping is invalid.");
        if ((uint)imageX >= (uint)ImageWidth || (uint)imageY >= (uint)ImageHeight)
            throw new ArgumentOutOfRangeException(nameof(imageX), "Coordinates must be inside the most recent screenshot.");

        // Map pixel centers so rounding stays inside the selected physical desktop bounds,
        // including desktops whose origin is negative due to monitor arrangement.
        var desktopX = DesktopBounds.Left + (int)Math.Floor((imageX + .5d) * DesktopBounds.Width / ImageWidth);
        var desktopY = DesktopBounds.Top + (int)Math.Floor((imageY + .5d) * DesktopBounds.Height / ImageHeight);
        return (Math.Min(desktopX, DesktopBounds.Right - 1), Math.Min(desktopY, DesktopBounds.Bottom - 1));
    }

    /// <summary>Maps an image-pixel rectangle (x/y/width/height) to the native desktop rectangle.</summary>
    public Rectangle ToDesktopBounds(Rectangle imageBounds)
    {
        if (!IsValid) throw new InvalidOperationException("The screenshot coordinate mapping is invalid.");
        if (imageBounds.Width <= 0 || imageBounds.Height <= 0 || imageBounds.Left < 0 || imageBounds.Top < 0
            || imageBounds.Right > ImageWidth || imageBounds.Bottom > ImageHeight)
            throw new ArgumentOutOfRangeException(nameof(imageBounds), "The target rectangle must be inside the associated screenshot.");

        var left = DesktopBounds.Left + (int)Math.Floor((double)imageBounds.Left * DesktopBounds.Width / ImageWidth);
        var top = DesktopBounds.Top + (int)Math.Floor((double)imageBounds.Top * DesktopBounds.Height / ImageHeight);
        var right = DesktopBounds.Left + (int)Math.Ceiling((double)imageBounds.Right * DesktopBounds.Width / ImageWidth);
        var bottom = DesktopBounds.Top + (int)Math.Ceiling((double)imageBounds.Bottom * DesktopBounds.Height / ImageHeight);
        return Rectangle.Intersect(DesktopBounds, Rectangle.FromLTRB(left, top, right, bottom));
    }
}
