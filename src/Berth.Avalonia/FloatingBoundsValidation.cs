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
/// <see cref="Window.Position"/>; per-monitor DPI subtleties are out of v1 scope. On a
/// platform without real windows the «screen» is the workspace's own area (spec TW-7.7,
/// DA-7.5) — use <see cref="CreateOverlayValidator"/> instead: it validates against the
/// workspace bounds in local coordinates, healing a layout carried over from the desktop with
/// screen coordinates.
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
    /// Creates a validator over the workspace area of a platform without real windows (spec
    /// TW-7.4, TW-7.7, DA-7.5): the browser's «screen» is the workspace, and saved bounds are
    /// workspace-local coordinates. Bounds whose visible intersection with the workspace falls
    /// below the threshold — including screen coordinates saved on the desktop — are replaced
    /// by the workspace rectangle inset on every side. The workspace must be laid out when the
    /// validator runs; without layout information bounds are accepted as-is (headless, tests).
    /// </summary>
    /// <param name="workspaceArea">The workspace control whose bounds play the role of the screen.</param>
    /// <param name="minVisibleFraction">Minimum visible fraction of the saved rectangle's area for the bounds to be kept.</param>
    /// <exception cref="ArgumentOutOfRangeException">The fraction is not within (0..1].</exception>
    public static BoundsValidator CreateOverlayValidator(Control workspaceArea, double minVisibleFraction = 0.5)
    {
        ArgumentNullException.ThrowIfNull(workspaceArea);
        if (!(minVisibleFraction > 0 && minVisibleFraction <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minVisibleFraction), minVisibleFraction, "The fraction must be within (0..1].");
        }

        return saved =>
        {
            var area = workspaceArea.Bounds.Size;
            if (area.Width <= 0 || area.Height <= 0)
            {
                return null; // no layout information — accept as saved (headless, tests)
            }

            if (!(saved.Width > 0) || !(saved.Height > 0))
            {
                return DefaultWithin(area);
            }

            var rect = new Rect(saved.X, saved.Y, saved.Width, saved.Height);
            var intersection = rect.Intersect(new Rect(area));
            var visible = intersection.Width * intersection.Height;
            return visible / (saved.Width * saved.Height) >= minVisibleFraction ? null : DefaultWithin(area);
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

    /// <summary>
    /// Default bounds within a workspace of the given size — the overlay counterpart of
    /// <see cref="DefaultRelativeTo"/> (spec TW-7.7, DA-7.5): the workspace rectangle inset on
    /// every side, with the inset shrunk for small workspaces.
    /// </summary>
    internal static FloatingBounds DefaultWithin(Size area)
    {
        var insetX = Math.Min(Inset, area.Width / 4);
        var insetY = Math.Min(Inset, area.Height / 4);
        return new FloatingBounds(
            insetX,
            insetY,
            Math.Max(BerthMetrics.MinPaneSize * 2, area.Width - (2 * insetX)),
            Math.Max(BerthMetrics.MinPaneSize * 2, area.Height - (2 * insetY)));
    }
}
