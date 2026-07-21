using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.LogicalTree;

namespace Berth.Controls;

/// <summary>
/// Splitter of the built-in leaf chrome: a <see cref="GridSplitter"/> with its own
/// ControlTheme — the template is a bare visual bar (the drag logic lives in the base
/// class), restyled by defining a theme resource keyed by this type or by selector styles
/// (docs/styling.md). The separator brush and the thickness follow the design tokens as
/// code bindings at Template priority: the thickness targets Width or Height depending on
/// the resize direction, which a theme cannot express per instance. The style key is this
/// type, so the application theme's GridSplitter template does not apply.
/// </summary>
public sealed class BerthSplitter : GridSplitter
{
    /// <summary>Creates the splitter.</summary>
    public BerthSplitter()
    {
    }

    /// <inheritdoc/>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // The no-include theme fallback (docs/styling.md): an application theme resource
        // wins through the implicit resolution; otherwise the built-in theme applies.
        BerthControlThemes.EnsureTheme(this);
    }
}

/// <summary>
/// Splitter helpers shared by the persistent grids of the workspace. Splitters are leaf chrome
/// (TW-9.13): not focusable, carrying no view-state. The drag itself is pure visualization
/// (ADR-0004); releasing after actual movement commits one core command computed from the
/// rendered bounds (TW-5.9).
/// </summary>
internal static class Splitters
{
    /// <summary>Creates a workspace splitter of fixed thickness for one resize axis.</summary>
    public static BerthSplitter Create(string name, GridResizeDirection direction)
    {
        var splitter = new BerthSplitter
        {
            Name = name,
            ResizeDirection = direction,
            Focusable = false, // keyboard resize would bypass the release commit
            MinWidth = 0, // theme minimums would widen the 4px separator
            MinHeight = 0,
        };
        // Template priority: the tokens stay the live defaults while application selector
        // styles (StyleTrigger priority) may override them (docs/styling.md).
        ThemeTokens.BindBrush(
            splitter,
            GridSplitter.BackgroundProperty,
            BerthThemeKeys.Separator,
            BerthBrushes.Separator,
            BindingPriority.Template);
        ThemeTokens.BindSize(
            splitter,
            direction == GridResizeDirection.Rows ? GridSplitter.HeightProperty : GridSplitter.WidthProperty,
            BerthThemeKeys.SplitterThickness,
            BerthMetrics.SplitterThickness,
            BindingPriority.Template);
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
