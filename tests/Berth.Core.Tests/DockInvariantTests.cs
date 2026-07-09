using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Dock-area invariants INV-D1…INV-D4 and INV-D6 (spec document-area, section 4): valid states
/// pass, each violation is reported with its id. INV-D5 becomes checkable with panel content
/// trees (backlog task 1.8).
/// </summary>
public class DockInvariantTests
{
    private static readonly FloatingBounds Bounds = new(10, 20, 800, 600);

    private static TabGroupNode Group(params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = tabs.Length == 0 ? null : tabs[0] };

    private static SplitChild Child(TabTreeNode node, double share) => new(node, share);

    private static SplitNode Row(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Row, Children = [.. children] };

    private static SplitNode Column(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Column, Children = [.. children] };

    private static DocumentWindowState Window(TabTreeNode root, string currentTabId) =>
        new(Bounds, root, currentTabId);

    private static LayoutState Layout(DockAreaState dockArea) =>
        LayoutState.Empty with { DockArea = dockArea };

    private static string[] ViolatedInvariants(LayoutState state) =>
        LayoutInvariants.Validate(state, new ToolWindowRegistry())
            .Select(v => v.InvariantId)
            .Distinct()
            .ToArray();

    [Fact]
    public void Valid_dock_area_produces_no_violations()
    {
        var layout = Layout(new DockAreaState
        {
            Root = Row(
                Child(Group("a"), 0.5),
                Child(Column(Child(Group("b"), 0.25), Child(Group("c"), 0.75)), 0.5)),
            CurrentTabId = "a",
            Windows = [Window(Group("w"), "w")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        });

        Assert.Empty(LayoutInvariants.Validate(layout, new ToolWindowRegistry()));
    }

    [Fact]
    public void INV_D1_empty_group_outside_the_root_is_reported()
    {
        var layout = Layout(new DockAreaState
        {
            Root = Row(Child(Group("a"), 0.5), Child(Group(), 0.5)),
            CurrentTabId = "a",
        });

        Assert.Equal(["INV-D1"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D1_split_with_fewer_than_two_children_is_reported()
    {
        // A one-child split inherently breaks the share sum too, hence INV-D3 alongside.
        var layout = Layout(new DockAreaState
        {
            Root = Row(Child(Group("a"), 0.5)),
            CurrentTabId = "a",
        });

        Assert.Equal(["INV-D1", "INV-D3"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D1_child_split_repeating_the_parent_orientation_is_reported()
    {
        var layout = Layout(new DockAreaState
        {
            Root = Row(
                Child(Group("a"), 0.5),
                Child(Row(Child(Group("b"), 0.5), Child(Group("c"), 0.5)), 0.5)),
            CurrentTabId = "a",
        });

        Assert.Equal(["INV-D1"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D2_duplicate_tab_across_hosts_is_reported()
    {
        var layout = Layout(new DockAreaState
        {
            Root = Group("x"),
            CurrentTabId = "x",
            Windows = [Window(Group("x"), "x")],
        });

        Assert.Equal(["INV-D2"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D2_duplicate_tab_within_one_group_is_reported()
    {
        var layout = Layout(new DockAreaState
        {
            Root = new TabGroupNode { Tabs = ["x", "x"], ActiveTabId = "x" },
            CurrentTabId = "x",
        });

        Assert.Equal(["INV-D2"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D3_share_outside_the_open_interval_is_reported()
    {
        var layout = Layout(new DockAreaState
        {
            Root = Row(Child(Group("a"), 0.0), Child(Group("b"), 1.0)),
            CurrentTabId = "a",
        });

        Assert.Equal(["INV-D3"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D3_nan_share_is_reported()
    {
        var layout = Layout(new DockAreaState
        {
            Root = Row(Child(Group("a"), double.NaN), Child(Group("b"), 0.5)),
            CurrentTabId = "a",
        });

        Assert.Equal(["INV-D3"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D3_share_sum_beyond_the_tolerance_is_reported()
    {
        var layout = Layout(new DockAreaState
        {
            Root = Row(Child(Group("a"), 0.5), Child(Group("b"), 0.4)),
            CurrentTabId = "a",
        });

        Assert.Equal(["INV-D3"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D3_share_sum_within_the_tolerance_is_valid()
    {
        var layout = Layout(new DockAreaState
        {
            Root = Row(Child(Group("a"), 0.5), Child(Group("b"), 0.5 + 1e-12)),
            CurrentTabId = "a",
        });

        Assert.Empty(LayoutInvariants.Validate(layout, new ToolWindowRegistry()));
    }

    [Fact]
    public void INV_D4_active_tab_not_in_the_group_is_reported()
    {
        var layout = Layout(new DockAreaState
        {
            Root = new TabGroupNode { Tabs = ["a", "b"], ActiveTabId = "ghost" },
            CurrentTabId = "a",
        });

        Assert.Equal(["INV-D4"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D4_empty_group_with_an_active_tab_is_reported()
    {
        var layout = Layout(new DockAreaState
        {
            Root = new TabGroupNode { Tabs = [], ActiveTabId = "x" },
        });

        Assert.Equal(["INV-D4"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D4_null_main_current_tab_with_tabs_is_reported()
    {
        var layout = Layout(new DockAreaState { Root = Group("a"), CurrentTabId = null });

        Assert.Equal(["INV-D4"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D4_unknown_main_current_tab_is_reported()
    {
        var layout = Layout(new DockAreaState { Root = Group("a"), CurrentTabId = "ghost" });

        Assert.Equal(["INV-D4"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D4_current_tab_not_active_in_its_group_is_reported()
    {
        var layout = Layout(new DockAreaState { Root = Group("a", "b"), CurrentTabId = "b" });

        Assert.Equal(["INV-D4"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D4_active_dock_host_beyond_the_window_list_is_reported()
    {
        var layout = Layout(new DockAreaState { ActiveDockHost = DockHost.DocumentWindow(0) });

        Assert.Equal(["INV-D4"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_D6_document_window_without_tabs_is_reported()
    {
        // The mandatory current tab of such a window dangles too, hence INV-D4 alongside.
        var layout = Layout(new DockAreaState
        {
            Windows = [Window(TabGroupNode.Empty, "stale")],
        });

        Assert.Equal(["INV-D4", "INV-D6"], ViolatedInvariants(layout));
    }
}
