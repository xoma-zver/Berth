using Avalonia;
using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// The screen-visibility <see cref="BoundsValidator"/> of the UI layer (spec TW-7.4, DA-7.4):
/// saved bounds whose visible intersection with the screens' working areas falls below the
/// threshold are replaced by a default rectangle positioned relative to the main window — its
/// bounds inset on every side, no cascading (the reference's programmatic
/// suggestChildFrameBounds path). The application passes the validator to
/// <see cref="LayoutApply.Apply"/>; without screen information (headless runs) saved bounds
/// are accepted as-is. Bounds are treated in the coordinate space of
/// <see cref="Window.Position"/>; per-monitor DPI subtleties are out of v1 scope.
/// </summary>
public static class FloatingBoundsValidation
{
    private const double Inset = 100;

    /// <summary>Creates a validator over the screens of the given main window (spec TW-7.4).</summary>
    /// <param name="mainWindow">The application's main window: the source of screen information and of the replacement position.</param>
    /// <param name="minVisibleFraction">Minimum visible fraction of the saved rectangle's area for the bounds to be kept.</param>
    /// <exception cref="ArgumentOutOfRangeException">The fraction is not within (0..1].</exception>
    public static BoundsValidator CreateValidator(Window mainWindow, double minVisibleFraction = 0.5)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        if (!(minVisibleFraction > 0 && minVisibleFraction <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minVisibleFraction), minVisibleFraction, "The fraction must be within (0..1].");
        }

        return saved =>
        {
            if (!(saved.Width > 0) || !(saved.Height > 0))
            {
                return DefaultRelativeTo(mainWindow);
            }

            var screens = mainWindow.Screens.All;
            if (screens.Count == 0)
            {
                return null; // no screen information — accept as saved (headless, tests)
            }

            var rect = new PixelRect(
                (int)Math.Round(saved.X, MidpointRounding.AwayFromZero),
                (int)Math.Round(saved.Y, MidpointRounding.AwayFromZero),
                Math.Max(1, (int)Math.Round(saved.Width, MidpointRounding.AwayFromZero)),
                Math.Max(1, (int)Math.Round(saved.Height, MidpointRounding.AwayFromZero)));
            double visible = 0;
            foreach (var screen in screens)
            {
                var intersection = screen.WorkingArea.Intersect(rect);
                visible += (double)intersection.Width * intersection.Height;
            }

            var area = (double)rect.Width * rect.Height;
            return visible / area >= minVisibleFraction ? null : DefaultRelativeTo(mainWindow);
        };
    }

    /// <summary>
    /// Default bounds relative to the main window (spec TW-7.4; = the reference's
    /// suggestChildFrameBounds): the window's rectangle inset on every side. Shared with the
    /// «Move to New Window» default and floating records without saved bounds (TW-5.6).
    /// </summary>
    internal static FloatingBounds DefaultRelativeTo(Window mainWindow) => new(
        mainWindow.Position.X + Inset,
        mainWindow.Position.Y + Inset,
        Math.Max(BerthMetrics.MinPaneSize * 4, mainWindow.ClientSize.Width - (2 * Inset)),
        Math.Max(BerthMetrics.MinPaneSize * 3, mainWindow.ClientSize.Height - (2 * Inset)));
}
