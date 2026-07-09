using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Normalization rules N1–N5 and the zone-level pass (spec DA-3.1, DA-3.2, DA-2.3), including
/// the instance-preservation contract: canonical input comes back as the same reference.
/// </summary>
public class TabTreeNormalizationTests
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

    // ---- N1 empty groups ----

    [Fact]
    public void DA_2_3_empty_root_group_survives_normalization()
    {
        var root = TabGroupNode.Empty;

        Assert.Same(root, TabTreeNormalization.Normalize(root));
    }

    [Fact]
    public void DA_3_1_N1_empty_non_root_group_is_removed_and_shares_renormalized()
    {
        var tree = Row(Child(Group("a"), 0.4), Child(Group(), 0.2), Child(Group("b"), 0.4));

        var result = Assert.IsType<SplitNode>(TabTreeNormalization.Normalize(tree));

        Assert.Equal(2, result.Children.Length);
        Assert.Equal("a", Assert.IsType<TabGroupNode>(result.Children[0].Node).Tabs[0]);
        Assert.Equal("b", Assert.IsType<TabGroupNode>(result.Children[1].Node).Tabs[0]);
        Assert.Equal(0.5, result.Children[0].Share, 12);
        Assert.Equal(0.5, result.Children[1].Share, 12);
    }

    // ---- N2 degenerate splits ----

    [Fact]
    public void DA_3_1_N2_childless_split_at_root_becomes_an_empty_group()
    {
        var result = TabTreeNormalization.Normalize(Row());

        var group = Assert.IsType<TabGroupNode>(result);
        Assert.True(group.Tabs.IsEmpty);
    }

    [Fact]
    public void DA_3_1_N2_single_child_split_at_root_is_replaced_by_its_child()
    {
        var inner = Group("a");

        Assert.Same(inner, TabTreeNormalization.Normalize(Row(Child(inner, 1.0))));
    }

    [Fact]
    public void DA_3_1_N2_single_child_split_inside_a_parent_promotes_the_child()
    {
        var tree = Row(Child(Group("a"), 0.5), Child(Column(Child(Group("b"), 1.0)), 0.5));

        var result = Assert.IsType<SplitNode>(TabTreeNormalization.Normalize(tree));

        Assert.Equal(2, result.Children.Length);
        Assert.Equal("b", Assert.IsType<TabGroupNode>(result.Children[1].Node).Tabs[0]);
        Assert.Equal(0.5, result.Children[1].Share, 12);
    }

    // ---- N3 merging ----

    [Fact]
    public void DA_3_1_N3_same_orientation_child_split_is_merged_with_scaled_shares()
    {
        var tree = Row(
            Child(Group("a"), 0.3),
            Child(Row(Child(Group("b"), 0.5), Child(Group("c"), 0.5)), 0.7));

        var result = Assert.IsType<SplitNode>(TabTreeNormalization.Normalize(tree));

        Assert.Equal(3, result.Children.Length);
        Assert.Equal(0.3, result.Children[0].Share, 12);
        Assert.Equal(0.35, result.Children[1].Share, 12);
        Assert.Equal(0.35, result.Children[2].Share, 12);
    }

    [Fact]
    public void DA_3_1_rules_cascade_like_the_tree_shape_of_DA_E8()
    {
        // Static shape of DA-E8: Row[A, Column[G(empty), Row[B, C]]] — N1 eats the group,
        // N2 promotes Row[B, C], N3 merges it into the outer Row with scaled shares.
        var tree = Row(
            Child(Group("a"), 0.4),
            Child(
                Column(
                    Child(Group(), 0.5),
                    Child(Row(Child(Group("b"), 0.5), Child(Group("c"), 0.5)), 0.5)),
                0.6));

        var result = Assert.IsType<SplitNode>(TabTreeNormalization.Normalize(tree));

        Assert.Equal(SplitOrientation.Row, result.Orientation);
        Assert.Equal(3, result.Children.Length);
        Assert.All(result.Children, c => Assert.IsType<TabGroupNode>(c.Node));
        Assert.Equal(0.4, result.Children[0].Share, 12);
        Assert.Equal(0.3, result.Children[1].Share, 12);
        Assert.Equal(0.3, result.Children[2].Share, 12);
    }

    // ---- N4 shares ----

    [Fact]
    public void DA_3_1_N4_nan_share_is_replaced_with_the_equal_share()
    {
        var tree = Row(Child(Group("a"), double.NaN), Child(Group("b"), 0.5));

        var result = Assert.IsType<SplitNode>(TabTreeNormalization.Normalize(tree));

        Assert.Equal(0.5, result.Children[0].Share, 12);
        Assert.Equal(0.5, result.Children[1].Share, 12);
    }

    [Fact]
    public void DA_3_1_N4_out_of_range_share_is_replaced_then_the_vector_is_renormalized()
    {
        var tree = Row(Child(Group("a"), 1.5), Child(Group("b"), 0.25));

        var result = Assert.IsType<SplitNode>(TabTreeNormalization.Normalize(tree));

        // 1.5 is invalid → 1/2; the vector [0.5, 0.25] sums to 0.75 → renormalized.
        Assert.Equal(2.0 / 3, result.Children[0].Share, 12);
        Assert.Equal(1.0 / 3, result.Children[1].Share, 12);
    }

    [Fact]
    public void DA_3_1_N4_share_sum_is_renormalized_to_one() // DA-E10 shape
    {
        var tree = Row(Child(Group("a"), 0.9), Child(Group("b"), 0.6));

        var result = Assert.IsType<SplitNode>(TabTreeNormalization.Normalize(tree));

        Assert.Equal(0.6, result.Children[0].Share, 12);
        Assert.Equal(0.4, result.Children[1].Share, 12);
    }

    [Fact]
    public void DA_3_1_N4_sum_within_the_tolerance_is_canonical()
    {
        var tree = Row(Child(Group("a"), 0.5), Child(Group("b"), 0.5 + 1e-12));

        Assert.Same(tree, TabTreeNormalization.Normalize(tree));
    }

    // ---- N5 group activity ----

    [Fact]
    public void DA_3_1_N5_active_tab_outside_the_group_is_replaced_by_the_first_tab()
    {
        var group = new TabGroupNode { Tabs = ["a", "b"], ActiveTabId = "ghost" };

        var result = Assert.IsType<TabGroupNode>(TabTreeNormalization.Normalize(group));

        Assert.Equal("a", result.ActiveTabId);
    }

    [Fact]
    public void DA_3_1_N5_null_active_tab_of_a_non_empty_group_becomes_the_first_tab()
    {
        var group = new TabGroupNode { Tabs = ["a", "b"], ActiveTabId = null };

        var result = Assert.IsType<TabGroupNode>(TabTreeNormalization.Normalize(group));

        Assert.Equal("a", result.ActiveTabId);
    }

    [Fact]
    public void DA_3_1_N5_active_tab_of_an_empty_group_becomes_null()
    {
        var group = new TabGroupNode { Tabs = [], ActiveTabId = "x" };

        var result = Assert.IsType<TabGroupNode>(TabTreeNormalization.Normalize(group));

        Assert.Null(result.ActiveTabId);
    }

    // ---- DA-3.2 instance preservation ----

    [Fact]
    public void DA_3_2_canonical_tree_is_returned_as_the_same_instance()
    {
        var tree = Row(
            Child(Group("a"), 0.5),
            Child(Column(Child(Group("b"), 0.25), Child(Group("c"), 0.75)), 0.5));

        Assert.Same(tree, TabTreeNormalization.Normalize(tree));
    }

    // ---- Zone level: document windows and host activity ----

    [Fact]
    public void DA_3_1_zone_removes_a_document_window_without_tabs_and_remaps_the_active_host()
    {
        var area = new DockAreaState
        {
            Windows = [Window(TabGroupNode.Empty, "stale"), Window(Group("w"), "w")],
            ActiveDockHost = DockHost.DocumentWindow(1),
        };

        var result = TabTreeNormalization.Normalize(area);

        var survivor = Assert.Single(result.Windows);
        Assert.Equal("w", survivor.CurrentTabId);
        Assert.Equal(DockHost.DocumentWindow(0), result.ActiveDockHost);
    }

    [Fact]
    public void DA_3_1_zone_active_host_of_a_removed_window_falls_back_to_the_main_window()
    {
        var area = new DockAreaState
        {
            Windows = [Window(TabGroupNode.Empty, "stale")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        };

        var result = TabTreeNormalization.Normalize(area);

        Assert.True(result.Windows.IsEmpty);
        Assert.Equal(DockHost.MainWindow, result.ActiveDockHost);
    }

    [Fact]
    public void DA_3_1_N5_active_host_with_an_unknown_index_falls_back_to_the_main_window()
    {
        var area = new DockAreaState { ActiveDockHost = DockHost.DocumentWindow(3) };

        Assert.Equal(DockHost.MainWindow, TabTreeNormalization.Normalize(area).ActiveDockHost);
    }

    [Fact]
    public void DA_3_1_N5_unknown_current_tab_falls_back_to_the_first_non_empty_group_active_tab()
    {
        // The first group in depth-first order has active tab "b" — the fallback takes the
        // group's active tab, not its first tab.
        var area = new DockAreaState
        {
            Root = Row(
                Child(new TabGroupNode { Tabs = ["a", "b"], ActiveTabId = "b" }, 0.5),
                Child(Group("c"), 0.5)),
            CurrentTabId = "ghost",
        };

        Assert.Equal("b", TabTreeNormalization.Normalize(area).CurrentTabId);
    }

    [Fact]
    public void DA_3_1_N5_null_main_current_tab_with_tabs_is_assigned()
    {
        var area = new DockAreaState { Root = Group("a"), CurrentTabId = null };

        Assert.Equal("a", TabTreeNormalization.Normalize(area).CurrentTabId);
    }

    [Fact]
    public void DA_3_1_N5_current_tab_not_active_in_its_group_becomes_the_group_active_tab()
    {
        var area = new DockAreaState { Root = Group("a", "b"), CurrentTabId = "b" };

        Assert.Equal("a", TabTreeNormalization.Normalize(area).CurrentTabId);
    }

    [Fact]
    public void DA_3_1_N5_window_current_tab_is_reassigned_within_the_window()
    {
        var area = new DockAreaState
        {
            Windows = [Window(Group("w1", "w2"), "ghost")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        };

        var result = TabTreeNormalization.Normalize(area);

        Assert.Equal("w1", result.Windows[0].CurrentTabId);
        Assert.Equal(DockHost.DocumentWindow(0), result.ActiveDockHost);
    }

    [Fact]
    public void DA_3_2_canonical_dock_area_is_returned_as_the_same_instance()
    {
        var area = new DockAreaState
        {
            Root = Group("a"),
            CurrentTabId = "a",
            Windows = [Window(Group("w"), "w")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        };

        Assert.Same(area, TabTreeNormalization.Normalize(area));
    }
}
