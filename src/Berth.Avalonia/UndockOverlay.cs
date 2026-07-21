using Avalonia;
using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Overlay layer of the open Undock tool windows (TW-3.3): each entry hugs the side of
/// its placement at full extent — a side overlay takes the whole workspace height, including
/// the bottom pane area; the bottom overlay takes the whole width between the stripes — with
/// the thickness given by the side's <see cref="SideState.Weight"/> (the docked layer and the
/// overlay share one side width), clamped to the render minimum (TW-2.8). The overlay paints
/// above the docked layout on an opaque backdrop — the panels underneath must not show
/// through — and never affects their sizes. Entries are keyed by window id and reconciled in
/// place (TW-9.13): toggling one overlay never touches the other entries or the docked
/// layer beneath.
/// </summary>
internal sealed class UndockOverlay : Panel
{
    private readonly Dictionary<string, OverlayBackdrop> _entries = new(StringComparer.Ordinal);

    public UndockOverlay() => Name = "PART_UndockOverlay";

    /// <summary>Reconciles the entries with the open overlay-layer windows of the state.</summary>
    public void Update(LayoutState state, Func<string, ToolWindowDecorator> hosts)
    {
        List<string>? stale = null;
        foreach (var id in _entries.Keys)
        {
            if (OverlayWindow(state, id) is null)
            {
                (stale ??= []).Add(id);
            }
        }

        if (stale is not null)
        {
            foreach (var id in stale)
            {
                var entry = _entries[id];
                // The host survives in the workspace cache (TW-9.13) and may join another
                // window of the workspace later — it leaves through the draining detach
                // (see BerthWorkspace.DetachFromParent).
                if (entry.Child is { } host)
                {
                    BerthWorkspace.DetachFromParent(host);
                }

                Children.Remove(entry);
                _entries.Remove(id);
            }
        }

        foreach (var window in state.ToolWindows)
        {
            if (!window.IsOpen || window.Mode.GetLayer() != ToolWindowLayer.Overlay)
            {
                continue;
            }

            if (!_entries.TryGetValue(window.Id, out var entry))
            {
                var host = hosts(window.Id);
                BerthWorkspace.DetachFromParent(host);
                entry = new OverlayBackdrop(host);
                _entries[window.Id] = entry;
                // Two open overlays of one side are legal (INV-2) but transient: autohide
                // closes the first one as the second takes focus (TW-6.1, phase 3), so — as in
                // IDEA — the z-order of that fleeting state is deliberately unspecified;
                // entries paint in arrival order.
                Children.Add(entry);
            }

            // The overlay thickness is the side's weight (TW-3.3): the docked layer and the
            // overlay share one side width, so the overlay exactly covers its docked neighbour.
            entry.Side = window.Slot.Side;
            entry.Weight = state.GetSide(window.Slot.Side).Weight;
        }

        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var entry in _entries.Values)
        {
            entry.Measure(SlotFor(entry.Side, entry.Weight, availableSize).Size);
        }

        // The overlay never demands size of its own: it covers whatever the workspace gives.
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var entry in _entries.Values)
        {
            entry.Arrange(SlotFor(entry.Side, entry.Weight, finalSize));
        }

        return finalSize;
    }

    private static ToolWindowState? OverlayWindow(LayoutState state, string id) =>
        state.ToolWindows.FirstOrDefault(w =>
            string.Equals(w.Id, id, StringComparison.Ordinal)
            && w.IsOpen
            && w.Mode.GetLayer() == ToolWindowLayer.Overlay);

    private static Rect SlotFor(ToolWindowSide side, double weight, Size area)
    {
        var width = Thickness(weight, area.Width);
        var height = Thickness(weight, area.Height);
        return side switch
        {
            ToolWindowSide.Left => new Rect(0, 0, width, area.Height),
            ToolWindowSide.Right => new Rect(area.Width - width, 0, width, area.Height),
            _ => new Rect(0, area.Height - height, area.Width, height),
        };
    }

    private static double Thickness(double weight, double extent) =>
        Math.Min(extent, Math.Max(BerthMetrics.MinPaneSize, weight * extent));

    /// <summary>
    /// Opaque backdrop of one overlay entry (TW-3.3): the default brushes are translucent,
    /// so without a backdrop the panels underneath would show through the overlay. The
    /// surface follows the token with a theme-variant default (<see cref="ThemeTokens.BindSurface"/>).
    /// </summary>
    private sealed class OverlayBackdrop : Border
    {
        public OverlayBackdrop(Control child)
        {
            Name = "PART_OverlayBackdrop";
            Child = child;
            ThemeTokens.BindSurface(this);
        }

        /// <summary>Side the entry hugs (TW-3.3).</summary>
        public ToolWindowSide Side { get; set; }

        /// <summary>The side weight giving the entry its thickness (TW-3.3).</summary>
        public double Weight { get; set; }
    }
}
