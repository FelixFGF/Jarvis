using System.Drawing;

namespace Jarvis.Services;

/// <summary>Validated visual target bound to one exact screenshot observation.</summary>
public sealed record VisualMouseTarget(string ObservationId, Rectangle ImageBounds, int ImageX, int ImageY, double Confidence);

public sealed record MappedMouseTarget(Rectangle DesktopBounds, Point IntendedDesktopPoint, int PositionTolerancePixels);

public interface IMouseInputController
{
    bool SetCursorPosition(Point position);
    Point GetCursorPosition();
    void SendLeftClick();
}

public static class VerifiedMouseClick
{
    public static Point Execute(IMouseInputController input, Point intended, Rectangle targetBounds,
        int tolerancePixels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (!targetBounds.Contains(intended)) throw new InvalidOperationException("The click point is outside the validated target bounds.");
        if (!input.SetCursorPosition(intended)) throw new InvalidOperationException("Windows did not accept the cursor position.");
        var positioned = input.GetCursorPosition();
        if (!ComputerMouseTargeting.CursorMatchesTarget(intended, positioned, targetBounds, tolerancePixels))
            throw new InvalidOperationException($"Cursor placement could not be verified inside the target. Requested ({intended.X},{intended.Y}), measured ({positioned.X},{positioned.Y}); no click was sent.");
        cancellationToken.ThrowIfCancellationRequested();
        var beforeDispatch = input.GetCursorPosition();
        if (!ComputerMouseTargeting.CursorMatchesTarget(intended, beforeDispatch, targetBounds, tolerancePixels))
            throw new InvalidOperationException($"Cursor moved before click dispatch. Requested ({intended.X},{intended.Y}), measured ({beforeDispatch.X},{beforeDispatch.Y}); no click was sent.");
        input.SendLeftClick();
        return input.GetCursorPosition();
    }
}

/// <summary>Pure validation and mapping for model-proposed visual mouse targets.</summary>
public static class ComputerMouseTargeting
{
    public const double MinimumVisualConfidence = 0.70;

    public static MappedMouseTarget Map(VisualMouseTarget target, string currentObservationId,
        ScreenshotCoordinateMap map, Rectangle? intendedWindowBounds = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(currentObservationId) || !string.Equals(target.ObservationId, currentObservationId, StringComparison.Ordinal))
            throw new InvalidOperationException("The target belongs to a different screenshot. Observe the screen again before clicking.");
        if (!map.IsValid) throw new InvalidOperationException("The screenshot-to-desktop mapping is unavailable.");
        if (target.Confidence is < MinimumVisualConfidence or > 1 || double.IsNaN(target.Confidence))
            throw new InvalidOperationException($"Visual target confidence must be at least {MinimumVisualConfidence:0.00}.");
        if (target.ImageBounds.Width < 3 || target.ImageBounds.Height < 3)
            throw new InvalidOperationException("The visual target is too small to click safely.");
        if (!target.ImageBounds.Contains(target.ImageX, target.ImageY))
            throw new InvalidOperationException("The proposed click point is outside the identified target.");

        var desktopBounds = map.ToDesktopBounds(target.ImageBounds);
        if (desktopBounds.Width <= 0 || desktopBounds.Height <= 0)
            throw new InvalidOperationException("The visual target maps to an empty desktop region.");
        if (intendedWindowBounds is { } window && !window.Contains(desktopBounds))
            throw new InvalidOperationException("The identified target does not lie entirely inside the observed foreground window.");

        var point = map.ToDesktop(target.ImageX, target.ImageY);
        if (!desktopBounds.Contains(point.X, point.Y))
            throw new InvalidOperationException("The mapped click point is outside the mapped target bounds.");

        // A tolerance is derived from the target's physical dimensions, with a small
        // cap. It validates cursor placement only; it never shifts the requested point.
        var tolerance = Math.Min(4, Math.Max(1, Math.Min(desktopBounds.Width, desktopBounds.Height) / 10));
        return new MappedMouseTarget(desktopBounds, new Point(point.X, point.Y), tolerance);
    }

    public static bool CursorMatchesTarget(Point intended, Point actual, Rectangle targetBounds, int tolerancePixels)
    {
        if (tolerancePixels < 0 || targetBounds.Width <= 0 || targetBounds.Height <= 0 || !targetBounds.Contains(actual)) return false;
        var dx = Math.Abs((long)actual.X - intended.X);
        var dy = Math.Abs((long)actual.Y - intended.Y);
        return dx <= tolerancePixels && dy <= tolerancePixels;
    }
}
