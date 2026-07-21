using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Splitter helpers shared by the persistent grids of the workspace. Splitters are leaf chrome
/// (TW-9.13): not focusable, carrying no view-state. The drag itself is pure visualization
/// (ADR-0004); releasing after actual movement commits one core command computed from the
/// rendered bounds (TW-5.9).
/// </summary>
internal static class Splitters
{
    /// <summary>Creates a workspace splitter of fixed thickness for one resize axis.</summary>
    public static GridSplitter Create(string name, GridResizeDirection direction)
    {
        var splitter = new GridSplitter
        {
            Name = name,
            ResizeDirection = direction,
            Focusable = false, // keyboard resize would bypass the release commit
            MinWidth = 0, // theme minimums would widen the 4px separator
            MinHeight = 0,
        };
        ThemeTokens.BindBrush(
            splitter, GridSplitter.BackgroundProperty, BerthThemeKeys.Separator, BerthBrushes.Separator);
        ThemeTokens.BindSize(
            splitter,
            direction == GridResizeDirection.Rows ? GridSplitter.HeightProperty : GridSplitter.WidthProperty,
            BerthThemeKeys.SplitterThickness,
            BerthMetrics.SplitterThickness);
        return splitter;
    }

    /// <summary>
    /// Wires a commit to run when a drag ends after actual movement: Thumb raises
    /// DragCompleted for a plain click too, which must not rewrite the state — no gesture,
    /// no command (ADR-0004); a clamped render would otherwise snap into the state and even
    /// layout rounding would drift it on every click.
    /// </summary>
    public static void CommitOnDragEnd(GridSplitter splitter, Action commit)
    {
        var moved = false;
        splitter.DragStarted += (_, _) => moved = false;
        splitter.DragDelta += (_, e) => moved |= e.Vector != default;
        splitter.DragCompleted += (_, _) =>
        {
            if (moved)
            {
                commit();
            }
        };
    }
}
