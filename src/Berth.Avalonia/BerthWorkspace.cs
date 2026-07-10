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
/// (TW-2.8). Input reduces to core commands (ADR-0004): a stripe icon click toggles openness
/// (TW-5.4), the decorator buttons close the window and open its menu (TW-5.3, TW-5.16), the
/// menus change modes and placement (TW-5.16), the «⋯» flyout restores hidden windows
/// (TW-8.3), and splitter drags are pure visuals until the release commits one resize command
/// (TW-5.9, TW-2.7 R2). Every command assigns its result back to <see cref="State"/> —
/// observe user-driven changes with <c>GetObservable(StateProperty)</c>. The subtree is
/// rebuilt from scratch on each change (a deliberately simple skeleton), reading titles and
/// icons from <see cref="Registry"/> at rebuild time (ADR-0003). With a
/// <see cref="Lifecycle"/> attached, decorator bodies materialize through the factory bridge
/// and every gesture command reports its transition to the coordinator (TW-9.3). Registry
/// mutations are invisible to the property system — and the live registration operations of
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

    /// <summary>Defines the <see cref="Lifecycle"/> property.</summary>
    public static readonly StyledProperty<ContentLifecycle?> LifecycleProperty =
        AvaloniaProperty.Register<BerthWorkspace, ContentLifecycle?>(nameof(Lifecycle));

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
    /// Optional content coordinator. When set, decorator bodies materialize through the
    /// factory bridge (spec TW-9.3, TW-9.5): a <see cref="Control"/> content object is hosted
    /// directly, anything else is presented by a <see cref="ContentControl"/> and resolved by
    /// the application's data templates. Every gesture command reports its transition to
    /// <see cref="ContentLifecycle.NotifyTransition"/> — exactly one call per command
    /// (ADR-0004); transitions the application performs itself — direct <see cref="State"/>
    /// assignments, Apply, ResetToDefaults — remain the application's to report. Null keeps
    /// the static skeleton: bodies stay placeholders.
    /// </summary>
    public ContentLifecycle? Lifecycle
    {
        get => GetValue(LifecycleProperty);
        set => SetValue(LifecycleProperty, value);
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

    /// <summary>
    /// Applies one core command to the live <see cref="State"/> and assigns the result back —
    /// the single funnel of every completed input gesture (ADR-0004). A no-op without a state.
    /// One gesture is one command, so the transition report satisfies the one-call-per-operation
    /// contract of <see cref="ContentLifecycle.NotifyTransition"/> by construction (TW-9.2).
    /// </summary>
    internal void Execute(Func<LayoutState, LayoutState> command)
    {
        if (State is { } state)
        {
            var result = command(state);
            // Assign first: the rebuild drops released content from the visual tree before the
            // coordinator hands it back to its factory.
            State = result;
            Lifecycle?.NotifyTransition(state, result);
        }
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty
            || change.Property == RegistryProperty
            || change.Property == LifecycleProperty)
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
        center.Children.Add(BuildCenter(state, registry, this));
        center.Children.Add(BuildOverlay(state, registry, this));

        var left = new ToolWindowStripe(QuickAccessSide.Left, state, registry, this);
        var right = new ToolWindowStripe(QuickAccessSide.Right, state, registry, this);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);

        var root = new DockPanel();
        root.Children.Add(left);
        root.Children.Add(right);
        root.Children.Add(center);
        Child = root;
    }

    /// <summary>The docked layout of TW-2.1: the bottom pane spans the full width, side panes and the dock area sit above it.</summary>
    private static Control BuildCenter(LayoutState state, ToolWindowRegistry registry, BerthWorkspace workspace)
    {
        var main = BuildMainRow(state, registry, workspace);
        if (!AnyOpenDocked(state, ToolWindowSide.Bottom))
        {
            return main;
        }

        return SplitterGrid.Build(
            main,
            new SidePane(ToolWindowSide.Bottom, state, registry, workspace),
            firstShare: 1 - state.Bottom.Weight,
            vertical: true,
            "PART_BottomSplitter",
            mainShare => workspace.Execute(s => s.SetSideSize(ToolWindowSide.Bottom, 1 - mainShare)));
    }

    /// <summary>Side panes and the dock area placeholder in one row; side widths follow the side weights (spec TW-2.5).</summary>
    private static Control BuildMainRow(LayoutState state, ToolWindowRegistry registry, BerthWorkspace workspace)
    {
        var dockArea = new Border { Name = "PART_DockArea" }; // the document area arrives in phase 4
        var leftOpen = AnyOpenDocked(state, ToolWindowSide.Left);
        var rightOpen = AnyOpenDocked(state, ToolWindowSide.Right);
        if (!leftOpen && !rightOpen)
        {
            return dockArea;
        }

        var grid = new Grid();
        var starPanes = new List<Control>(); // the star-sized cells: side panes and the dock area
        void Add(Control control, GridLength width, double minWidth)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width, MinWidth = minWidth });
            Grid.SetColumn(control, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(control);
        }

        // The drag is pure visualization (ADR-0004); the release commits the side weight — the
        // pane's share of the star-sized cells of the row — as one SetSideSize (TW-5.9, TW-2.6).
        Control Splitter(string name, ToolWindowSide side, Control pane)
        {
            var splitter = new GridSplitter
            {
                Name = name,
                Width = BerthMetrics.SplitterThickness,
                Background = BerthBrushes.Separator,
                ResizeDirection = GridResizeDirection.Columns,
                Focusable = false, // keyboard resize would bypass the release commit
                MinWidth = 0, // theme minimums would widen the 4px separator
                MinHeight = 0,
            };
            SplitterGrid.CommitOnDragEnd(splitter, () =>
            {
                var total = starPanes.Sum(p => p.Bounds.Width);
                if (total > 0)
                {
                    var weight = BerthMetrics.ClampFraction(pane.Bounds.Width / total);
                    workspace.Execute(s => s.SetSideSize(side, weight));
                }
            });
            return splitter;
        }

        var leftWeight = leftOpen ? state.Left.Weight : 0;
        var rightWeight = rightOpen ? state.Right.Weight : 0;
        if (leftOpen)
        {
            var pane = new SidePane(ToolWindowSide.Left, state, registry, workspace);
            starPanes.Add(pane);
            Add(pane, new GridLength(leftWeight, GridUnitType.Star), BerthMetrics.MinPaneSize);
            Add(Splitter("PART_LeftSideSplitter", ToolWindowSide.Left, pane), GridLength.Auto, 0);
        }

        starPanes.Add(dockArea);
        Add(dockArea, new GridLength(Math.Max(0, 1 - leftWeight - rightWeight), GridUnitType.Star), BerthMetrics.MinPaneSize);
        if (rightOpen)
        {
            var pane = new SidePane(ToolWindowSide.Right, state, registry, workspace);
            starPanes.Add(pane);
            Add(Splitter("PART_RightSideSplitter", ToolWindowSide.Right, pane), GridLength.Auto, 0);
            Add(pane, new GridLength(rightWeight, GridUnitType.Star), BerthMetrics.MinPaneSize);
        }

        return grid;
    }

    private static UndockOverlay BuildOverlay(LayoutState state, ToolWindowRegistry registry, BerthWorkspace workspace)
    {
        // Two open overlays of one side are legal (INV-2) but transient: autohide closes the
        // first one as the second takes focus (TW-6.1, phase 3), so — as in IDEA — the z-order
        // of that fleeting state is deliberately unspecified; entries render in state order.
        var overlay = new UndockOverlay();
        foreach (var window in state.ToolWindows.Where(w => w.IsOpen && w.Mode.GetLayer() == ToolWindowLayer.Overlay))
        {
            // The overlay thickness is the side's weight (TW-3.3): the docked layer and the
            // overlay share one side width, so the overlay exactly covers its docked neighbour.
            overlay.AddOverlay(
                ToolWindowDecorator.For(window, registry, workspace),
                window.Slot.Side,
                state.GetSide(window.Slot.Side).Weight);
        }

        return overlay;
    }

    private static bool AnyOpenDocked(LayoutState state, ToolWindowSide side) =>
        state.ToolWindows.Any(w =>
            w.IsOpen && w.Slot.Side == side && w.Mode.GetLayer() == ToolWindowLayer.Docked);
}
