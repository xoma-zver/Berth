using Avalonia;
using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Root control materializing a <see cref="LayoutState"/> (spec TW-2.1): the stripes at the
/// left and right edges; between them the side panes and the dock area above the bottom pane,
/// which spans the full width between the stripes; open Undock windows overlay the workspace
/// (TW-3.3). Float and Window modes are not materialized until the floating-window phase —
/// only their stripe icons show. The control is a pure projection of the state (ADR-0002):
/// fractions become pixels here and render-time minimums clamp without touching the state
/// (TW-2.8); no input is handled — gestures reduce to core commands in later tasks (ADR-0004).
/// Assign the result of every core command back to <see cref="State"/> to refresh: the subtree
/// is rebuilt from scratch on each change (a deliberately simple skeleton), reading titles and
/// icons from <see cref="Registry"/> at rebuild time (ADR-0003). Registry mutations are
/// invisible to the property system — and the live registration operations of
/// <see cref="ContentLifecycle"/> may return a value-equal state, which assignment
/// deduplicates — so call <see cref="Refresh"/> after them.
/// </summary>
public sealed class BerthWorkspace : Decorator
{
    /// <summary>Defines the <see cref="State"/> property.</summary>
    public static readonly StyledProperty<LayoutState?> StateProperty =
        AvaloniaProperty.Register<BerthWorkspace, LayoutState?>(nameof(State));

    /// <summary>Defines the <see cref="Registry"/> property.</summary>
    public static readonly StyledProperty<ToolWindowRegistry?> RegistryProperty =
        AvaloniaProperty.Register<BerthWorkspace, ToolWindowRegistry?>(nameof(Registry));

    /// <summary>The layout to materialize; null renders nothing.</summary>
    public LayoutState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Registrations supplying titles and icons (spec TW-9.1); null renders nothing.</summary>
    public ToolWindowRegistry? Registry
    {
        get => GetValue(RegistryProperty);
        set => SetValue(RegistryProperty, value);
    }

    /// <summary>
    /// Rebuilds the projection from the current <see cref="State"/> and <see cref="Registry"/>.
    /// Required after operations that mutate the registry in place — the live registration
    /// lifecycle (<see cref="ContentLifecycle.Register"/>, RegisterDockContent, Unregister):
    /// they may return a state value-equal to the current one while titles, icons and claims
    /// changed, and the property system deduplicates equal assignments. Core layout commands
    /// need no explicit refresh beyond assigning their result to <see cref="State"/>.
    /// </summary>
    public void Refresh() => Rebuild();

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty || change.Property == RegistryProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        if (State is not { } state || Registry is not { } registry)
        {
            Child = null;
            return;
        }

        // The overlay is the second child, painting above the docked layout (TW-3.3).
        var center = new Panel();
        center.Children.Add(BuildCenter(state, registry));
        center.Children.Add(BuildOverlay(state, registry));

        var left = new ToolWindowStripe(QuickAccessSide.Left, state, registry);
        var right = new ToolWindowStripe(QuickAccessSide.Right, state, registry);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);

        var root = new DockPanel();
        root.Children.Add(left);
        root.Children.Add(right);
        root.Children.Add(center);
        Child = root;
    }

    /// <summary>The docked layout of TW-2.1: the bottom pane spans the full width, side panes and the dock area sit above it.</summary>
    private static Control BuildCenter(LayoutState state, ToolWindowRegistry registry)
    {
        var main = BuildMainRow(state, registry);
        if (!AnyOpenDocked(state, ToolWindowSide.Bottom))
        {
            return main;
        }

        return SplitterGrid.Build(
            main,
            new SidePane(ToolWindowSide.Bottom, state, registry),
            firstShare: 1 - state.Bottom.Weight,
            vertical: true,
            "PART_BottomSplitter");
    }

    /// <summary>Side panes and the dock area placeholder in one row; side widths follow the side weights (spec TW-2.5).</summary>
    private static Control BuildMainRow(LayoutState state, ToolWindowRegistry registry)
    {
        var dockArea = new Border { Name = "PART_DockArea" }; // the document area arrives in phase 4
        var leftOpen = AnyOpenDocked(state, ToolWindowSide.Left);
        var rightOpen = AnyOpenDocked(state, ToolWindowSide.Right);
        if (!leftOpen && !rightOpen)
        {
            return dockArea;
        }

        var grid = new Grid();
        void Add(Control control, GridLength width, double minWidth)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width, MinWidth = minWidth });
            Grid.SetColumn(control, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(control);
        }

        // Splitters are named by side: task 2.2 reduces each one's drag to SetSideSize of its side.
        Control Splitter(string name) => new Border
        {
            Name = name,
            Width = BerthMetrics.SplitterThickness,
            Background = BerthBrushes.Separator,
        };

        var leftWeight = leftOpen ? state.Left.Weight : 0;
        var rightWeight = rightOpen ? state.Right.Weight : 0;
        if (leftOpen)
        {
            Add(new SidePane(ToolWindowSide.Left, state, registry), new GridLength(leftWeight, GridUnitType.Star), BerthMetrics.MinPaneSize);
            Add(Splitter("PART_LeftSideSplitter"), GridLength.Auto, 0);
        }

        Add(dockArea, new GridLength(Math.Max(0, 1 - leftWeight - rightWeight), GridUnitType.Star), BerthMetrics.MinPaneSize);
        if (rightOpen)
        {
            Add(Splitter("PART_RightSideSplitter"), GridLength.Auto, 0);
            Add(new SidePane(ToolWindowSide.Right, state, registry), new GridLength(rightWeight, GridUnitType.Star), BerthMetrics.MinPaneSize);
        }

        return grid;
    }

    private static UndockOverlay BuildOverlay(LayoutState state, ToolWindowRegistry registry)
    {
        // Two open overlays of one side are legal (INV-2) but transient: autohide closes the
        // first one as the second takes focus (TW-6.1, phase 3), so — as in IDEA — the z-order
        // of that fleeting state is deliberately unspecified; entries render in state order.
        var overlay = new UndockOverlay();
        foreach (var window in state.ToolWindows.Where(w => w.IsOpen && w.Mode.GetLayer() == ToolWindowLayer.Overlay))
        {
            overlay.AddOverlay(ToolWindowDecorator.For(window, registry), window.Slot.Side, window.UndockWeight);
        }

        return overlay;
    }

    private static bool AnyOpenDocked(LayoutState state, ToolWindowSide side) =>
        state.ToolWindows.Any(w =>
            w.IsOpen && w.Slot.Side == side && w.Mode.GetLayer() == ToolWindowLayer.Docked);
}
