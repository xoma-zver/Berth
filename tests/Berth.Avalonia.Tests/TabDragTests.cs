using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Berth;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Tab drag gestures (spec DA-9.7; ADR-0004): the click/drag threshold, strip insertion
/// zones, the diagonal edge wedges and group centers with their command mapping, canHost
/// filtering of the target catalog, identity drops (DA-E40), the no-trace cancellation
/// closing DA-E22 by gesture, the deferred press-focus of tab headers, gesture survival
/// across external state changes and the pointer-path auto-hide exemption (TW-6.2).
/// </summary>
public class TabDragTests
{
    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static TabGroupNode Group(string? active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    private static SplitChild Child(TabTreeNode node, double share) => new(node, share);

    private static SplitNode RowSplit(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Row, Children = [.. children] };

    private static LayoutState DockState(TabTreeNode root, string? current) =>
        LayoutState.Empty with { DockArea = new DockAreaState { Root = root, CurrentTabId = current } };

    /// <summary>Registry with dock content claiming the "d" prefix (spec TW-9.11).</summary>
    private static ToolWindowRegistry DockRegistry()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new CountingTabFactory("d"));
        return registry;
    }

    /// <summary>Adds a tool window whose tab factory claims the given prefix (spec TW-9.11).</summary>
    private static ToolWindowRegistry WithPanel(ToolWindowRegistry registry, string panelId, string tabPrefix)
    {
        registry.Register(new ToolWindowDescriptor(
            panelId, panelId, new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            TabFactory = new CountingTabFactory(tabPrefix),
        });
        return registry;
    }

    /// <summary>Presses at <paramref name="from"/> and moves past the threshold to <paramref name="to"/> without releasing.</summary>
    private static void DragTo(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Release(Window window, Point at)
    {
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The group view hosting the given attached tab host — wedge and center geometry reads its bounds.</summary>
    private static Control GroupViewOf(Window window, string activeTabId)
    {
        var content = (Control)TabHost(window, activeTabId).GetVisualParent()!;
        return (Control)content.GetVisualParent()!;
    }

    private static int CountStateChanges(BerthWorkspace workspace, int[] counter)
    {
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.Property == BerthWorkspace.StateProperty)
            {
                counter[0]++;
            }
        };
        return counter[0];
    }

    // ---- the threshold: click vs drag (TW-5.17, DA-9.7) ----

    [AvaloniaFact]
    public void DA_9_7_sub_threshold_movement_stays_a_click()
    {
        var window = Show(DockState(Group("d1", "d1", "d2"), "d1"), DockRegistry());

        var start = Center(TabHeader(window, "d2"), window);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X + 3, start.Y + 2));
        Release(window, new Point(start.X + 3, start.Y + 2));

        Assert.Equal("d2", St(window).DockArea.CurrentTabId); // the click activated (DA-5.3)
    }

    // ---- strip insertion zones (DA-9.7, DA-5.4) ----

    [AvaloniaFact]
    public void DA_9_7_strip_drop_into_another_group_commits_one_move()
    {
        var state = DockState(
            RowSplit(Child(Group("d1", "d1", "d2"), 0.5), Child(Group("d3", "d3"), 0.5)), "d1");
        var window = Show(state, DockRegistry());
        var workspace = Workspace(window);
        var changes = new int[1];
        CountStateChanges(workspace, changes);

        var start = Center(TabHeader(window, "d2"), window);
        var after = BoundsIn(TabHeader(window, "d3"), window);
        var target = new Point(after.Right + 10, after.Center.Y);
        DragTo(window, start, target);
        Assert.Equal(0, changes[0]); // pure visualization until the release (ADR-0004)
        Release(window, target);

        Assert.Equal(1, changes[0]); // one MoveTab; same host — activity follows the command
        var root = Assert.IsType<SplitNode>(St(window).DockArea.Root);
        Assert.Equal(2, root.Children.Length); // the strip beats the Up wedge — no split
        var receiver = Assert.IsType<TabGroupNode>(root.Children[1].Node);
        Assert.Equal(["d3", "d2"], receiver.Tabs);
        Assert.Equal("d2", receiver.ActiveTabId);
        Assert.Equal("d2", St(window).DockArea.CurrentTabId);
    }

    [AvaloniaFact]
    public void DA_9_7_strip_reorder_to_the_group_front()
    {
        var window = Show(DockState(Group("d1", "d1", "d2", "d3"), "d1"), DockRegistry());

        var start = Center(TabHeader(window, "d3"), window);
        var first = BoundsIn(TabHeader(window, "d1"), window);
        var target = new Point(first.Left + 2, first.Center.Y); // before the first header's midpoint
        DragTo(window, start, target);
        Release(window, target);

        var root = Assert.IsType<TabGroupNode>(St(window).DockArea.Root);
        Assert.Equal(["d3", "d1", "d2"], root.Tabs);
        Assert.Equal("d3", root.ActiveTabId);
    }

    // ---- identity drops (DA-E40) ----

    [AvaloniaFact]
    public void DA_E40_drop_right_after_own_header_is_identity()
    {
        var window = Show(DockState(Group("d2", "d1", "d2"), "d2"), DockRegistry());
        var before = St(window);

        var own = BoundsIn(TabHeader(window, "d2"), window);
        var target = new Point(own.Right + 8, own.Center.Y); // the gap after itself
        DragTo(window, own.Center, target);
        Assert.True(Part(window, "PART_DropMarker").IsVisible); // the zone exists — the drop is an identity, not a miss
        Release(window, target);

        Assert.Same(before, St(window)); // no command, no activation, no focus (DA-E40)
    }

    [AvaloniaFact]
    public void DA_E40_center_of_the_own_group_is_identity()
    {
        var window = Show(DockState(Group("d2", "d1", "d2"), "d2"), DockRegistry());
        var before = St(window);

        var start = Center(TabHeader(window, "d2"), window);
        var target = BoundsIn(GroupViewOf(window, "d2"), window).Center;
        DragTo(window, start, target);
        Assert.True(Part(window, "PART_DropMarker").IsVisible); // the center zone exists
        Release(window, target);

        Assert.Same(before, St(window));
    }

    // ---- group centers (DA-9.7) ----

    [AvaloniaFact]
    public void DA_9_7_center_of_another_group_moves_to_its_end()
    {
        var state = DockState(
            RowSplit(Child(Group("d1", "d1", "d2"), 0.5), Child(Group("d3", "d3", "d4"), 0.5)), "d1");
        var window = Show(state, DockRegistry());

        var start = Center(TabHeader(window, "d1"), window);
        var target = BoundsIn(GroupViewOf(window, "d3"), window).Center;
        DragTo(window, start, target);
        Release(window, target);

        var root = Assert.IsType<SplitNode>(St(window).DockArea.Root);
        var donor = Assert.IsType<TabGroupNode>(root.Children[0].Node);
        var receiver = Assert.IsType<TabGroupNode>(root.Children[1].Node);
        Assert.Equal(["d2"], donor.Tabs);
        Assert.Equal(["d3", "d4", "d1"], receiver.Tabs);
        Assert.Equal("d1", receiver.ActiveTabId);
        Assert.Equal("d1", St(window).DockArea.CurrentTabId);
    }

    // ---- edge wedges (DA-9.7, DA-5.5) ----

    [AvaloniaFact]
    public void DA_9_7_own_bottom_wedge_splits_the_group()
    {
        var window = Show(DockState(Group("d1", "d1", "d2"), "d1"), DockRegistry());

        var start = Center(TabHeader(window, "d2"), window);
        var rect = BoundsIn(GroupViewOf(window, "d1"), window);
        var target = new Point(rect.Center.X, rect.Bottom - 4);
        DragTo(window, start, target);
        Release(window, target);

        var root = Assert.IsType<SplitNode>(St(window).DockArea.Root);
        Assert.Equal(SplitOrientation.Column, root.Orientation);
        Assert.Equal(["d1"], Assert.IsType<TabGroupNode>(root.Children[0].Node).Tabs);
        Assert.Equal(["d2"], Assert.IsType<TabGroupNode>(root.Children[1].Node).Tabs);
        Assert.Equal("d2", St(window).DockArea.CurrentTabId);
    }

    [AvaloniaFact]
    public void DA_9_7_wedges_split_the_corner_by_the_diagonal()
    {
        // Two points near the bottom-left corner, on either side of the diagonal: the one
        // nearer the bottom edge splits Down, the one nearer the left edge splits Left.
        var below = Show(DockState(Group("d1", "d1", "d2"), "d1"), DockRegistry());
        var rect = BoundsIn(GroupViewOf(below, "d1"), below);
        DragTo(below, Center(TabHeader(below, "d2"), below), new Point(rect.Left + 24, rect.Bottom - 6));
        Release(below, new Point(rect.Left + 24, rect.Bottom - 6));
        Assert.Equal(
            SplitOrientation.Column, Assert.IsType<SplitNode>(St(below).DockArea.Root).Orientation);

        var beside = Show(DockState(Group("d1", "d1", "d2"), "d1"), DockRegistry());
        rect = BoundsIn(GroupViewOf(beside, "d1"), beside);
        DragTo(beside, Center(TabHeader(beside, "d2"), beside), new Point(rect.Left + 6, rect.Bottom - 24));
        Release(beside, new Point(rect.Left + 6, rect.Bottom - 24));
        var root = Assert.IsType<SplitNode>(St(beside).DockArea.Root);
        Assert.Equal(SplitOrientation.Row, root.Orientation);
        Assert.Equal(["d2"], Assert.IsType<TabGroupNode>(root.Children[0].Node).Tabs); // Left inserts before
    }

    [AvaloniaFact]
    public void DA_E41_foreign_wedge_is_move_plus_split()
    {
        var state = DockState(
            RowSplit(Child(Group("d1", "d1", "d2"), 0.5), Child(Group("d3", "d3"), 0.5)), "d1");
        var window = Show(state, DockRegistry());

        var start = Center(TabHeader(window, "d1"), window);
        var rect = BoundsIn(GroupViewOf(window, "d3"), window);
        var target = new Point(rect.Right - 4, rect.Center.Y);
        DragTo(window, start, target);
        Release(window, target);

        // MoveTab(d1 → G2's end) + SplitTab(d1, Right): Row[G(d2) 0.5, G(d3) 0.25, G(d1) 0.25].
        var root = Assert.IsType<SplitNode>(St(window).DockArea.Root);
        Assert.Equal(3, root.Children.Length);
        Assert.Equal(["d2"], Assert.IsType<TabGroupNode>(root.Children[0].Node).Tabs);
        Assert.Equal(["d3"], Assert.IsType<TabGroupNode>(root.Children[1].Node).Tabs);
        Assert.Equal(["d1"], Assert.IsType<TabGroupNode>(root.Children[2].Node).Tabs);
        Assert.Equal(0.5, root.Children[0].Share, 6);
        Assert.Equal(0.25, root.Children[1].Share, 6);
        Assert.Equal(0.25, root.Children[2].Share, 6);
        Assert.Equal("d1", St(window).DockArea.CurrentTabId);
    }

    // ---- canHost filtering (DA-9.7, INV-D5) ----

    [AvaloniaFact]
    public void DA_9_7_documents_and_sleeping_tabs_have_no_targets_in_panel_trees()
    {
        var registry = WithPanel(DockRegistry(), "p", "t");
        var state = DockState(Group("d1", "d1", "s1"), "d1") with
        {
            ToolWindows =
            [
                Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    ContentTree = Group("t1", "t1"),
                },
            ],
        };
        var window = Show(state, registry);
        var before = St(window);
        var panelContent = BoundsIn(Part(Decorator(window, "p"), "PART_GroupContent"), window).Center;

        // A document has no zones in a panel tree (a release there would be the outside
        // take-out of task 6.2 — cancelled here to keep the state comparable).
        DragTo(window, Center(TabHeader(window, "d1"), window), panelContent);
        Assert.False(Part(window, "PART_DropMarker").IsVisible); // the panel offers no zone
        PressEscape(window);
        Release(window, panelContent);
        Assert.Same(before, St(window));

        // An unclaimed (sleeping) tab has none either: its owner is not confirmed (INV-D5).
        DragTo(window, Center(TabHeader(window, "s1"), window), panelContent);
        Assert.False(Part(window, "PART_DropMarker").IsVisible);
        PressEscape(window);
        Release(window, panelContent);
        Assert.Same(before, St(window));
    }

    [AvaloniaFact]
    public void DA_9_7_panel_tab_drops_into_the_dock_area_with_activation()
    {
        var registry = WithPanel(DockRegistry(), "p", "t");
        var state = DockState(Group("d1", "d1"), "d1") with
        {
            ToolWindows =
            [
                Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    ContentTree = Group("t1", "p", "t1"),
                },
            ],
            ActiveToolWindowId = "p",
        };
        var window = Show(state, registry);

        // The source is the strip in the decorator header row (TW-9.5).
        var start = Center(TabHeader(window, "t1"), window);
        var target = BoundsIn(GroupViewOf(window, "d1"), window).Center;
        DragTo(window, start, target);
        Release(window, target);

        var root = Assert.IsType<TabGroupNode>(St(window).DockArea.Root);
        Assert.Equal(["d1", "t1"], root.Tabs);
        Assert.Equal("t1", St(window).DockArea.CurrentTabId); // activity followed the move (DA-5.4)
        Assert.Null(St(window).ActiveToolWindowId); // the cross-host ActivateTab cleared it (TW-6.5)
        Assert.Equal(["p"], ((TabGroupNode)St(window).ToolWindows[0].ContentTree).Tabs);
    }

    [AvaloniaFact]
    public void DA_9_7_dock_tab_returns_to_its_confirmed_owner_panel()
    {
        var registry = WithPanel(DockRegistry(), "p", "t");
        var state = DockState(Group("t1", "d1", "t1"), "t1") with
        {
            ToolWindows =
            [
                Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
            ],
        };
        var window = Show(state, registry);

        var start = Center(TabHeader(window, "t1"), window);
        // The empty root group of the panel offers only its center (DA-9.7).
        var target = BoundsIn(Part(Decorator(window, "p"), "PART_GroupContent"), window).Center;
        DragTo(window, start, target);
        Release(window, target);

        var tree = Assert.IsType<TabGroupNode>(St(window).ToolWindows[0].ContentTree);
        Assert.Equal(["t1"], tree.Tabs);
        Assert.Equal("p", St(window).ActiveToolWindowId); // activation followed the drop (DA-5.3)
        Assert.Equal("d1", St(window).DockArea.CurrentTabId); // the donor fallback (DA-6.3)
    }

    // ---- cancellation and DA-E22 ----

    [AvaloniaFact]
    public void DA_E22_cancelled_tab_drag_leaves_no_trace()
    {
        var window = Show(DockState(Group("d1", "d1", "d2"), "d1"), DockRegistry());
        var before = St(window);

        var start = Center(TabHeader(window, "d2"), window);
        var rect = BoundsIn(GroupViewOf(window, "d1"), window);
        DragTo(window, start, new Point(rect.Center.X, rect.Bottom - 4)); // over a valid wedge
        var drag = Workspace(window).Drag!;
        Assert.True(drag.GhostVisible); // the windowed ghost is an OS window (task 6.2)

        PressEscape(window);
        Release(window, new Point(rect.Center.X, rect.Bottom - 4));

        Assert.Same(before, St(window)); // zero commands: d1 stays active and current
        Assert.Equal("d1", St(window).DockArea.CurrentTabId);
        Assert.False(drag.GhostVisible);
    }

    // ---- the deferred press-focus of tab headers (DA-9.7) ----

    [AvaloniaFact]
    public void DA_9_7_panel_tab_press_does_not_activate_until_the_release()
    {
        var registry = WithPanel(DockRegistry(), "p", "t");
        var state = DockState(Group("d1", "d1"), "d1") with
        {
            ToolWindows =
            [
                Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    ContentTree = Group("t1", "p", "t1"),
                },
            ],
        };
        var window = Show(state, registry);
        var before = St(window);

        // The press alone parks no focus and runs no command (the deferral of DA-9.7).
        var start = Center(TabHeader(window, "t1"), window);
        window.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(before, St(window));
        Assert.Null(St(window).ActiveToolWindowId);

        // A drag cancelled out of the gesture leaves no activation behind (TW-5.17).
        window.MouseMove(new Point(start.X + 40, start.Y + 60));
        Dispatcher.UIThread.RunJobs();
        PressEscape(window);
        Release(window, new Point(start.X + 40, start.Y + 60));
        Assert.Same(before, St(window));

        // A plain click completes on the release: the tab activates its panel (DA-5.3).
        Click(window, TabHeader(window, "t1"));
        Assert.Equal("p", St(window).ActiveToolWindowId);
    }

    // ---- gesture vs external state changes (TW-5.17) ----

    [AvaloniaFact]
    public void DA_9_7_external_change_mid_drag_rebuilds_targets_and_the_gesture_continues()
    {
        var state = DockState(
            RowSplit(Child(Group("d1", "d1", "d2"), 0.5), Child(Group("d3", "d3"), 0.5)), "d1");
        var window = Show(state, DockRegistry());
        var workspace = Workspace(window);

        var start = Center(TabHeader(window, "d2"), window);
        DragTo(window, start, BoundsIn(GroupViewOf(window, "d3"), window).Center);

        // An external change mid-gesture: d1 closes, the first group re-projects.
        workspace.State = workspace.State!.CloseTab("d1");
        Dispatcher.UIThread.RunJobs();

        // The gesture continues over the updated geometry: drop after d3's header.
        var after = BoundsIn(TabHeader(window, "d3"), window);
        var target = new Point(after.Right + 10, after.Center.Y);
        window.MouseMove(target);
        Dispatcher.UIThread.RunJobs();
        Release(window, target);

        // The move emptied the first group; the root collapsed to the receiver (N1, N2).
        var root = Assert.IsType<TabGroupNode>(St(window).DockArea.Root);
        Assert.Equal(["d3", "d2"], root.Tabs);
        Assert.Equal("d2", root.ActiveTabId);
    }

    // ---- auto-hide integration (TW-6.2) ----

    [AvaloniaFact]
    public void TW_6_2_tab_drag_release_is_not_a_click_for_auto_hide()
    {
        var registry = WithPanel(DockRegistry(), "u", "t");
        var state = DockState(Group("d1", "d1", "d2"), "d1") with
        {
            ToolWindows =
            [
                Win("u", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.DockUnpinned,
                    LastInternalMode = ToolWindowMode.DockUnpinned,
                },
            ],
        };
        var window = Show(state, registry);

        // A document drag released over the unpinned panel: no zones there (canHost), so the
        // outside take-out moves the tab into a new document window (task 6.2) — and the
        // release must not close the panel by the pointer path (TW-6.2).
        var target = Center(Decorator(window, "u"), window);
        DragTo(window, Center(TabHeader(window, "d2"), window), target);
        Release(window, target);

        Assert.True(St(window).ToolWindows[0].IsOpen); // the drag release is not a click
        var docWindow = Assert.Single(St(window).DockArea.Windows);
        Assert.Equal(["d2"], Assert.IsType<TabGroupNode>(docWindow.Root).Tabs);
    }
}
