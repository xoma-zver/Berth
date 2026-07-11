using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Berth;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Materialization of the main window's dock-area tree (spec DA-2.1, DA-2.2, DA-9.4, DA-9.6):
/// groups and splits render at their shares, minimums clamp at render only, sleeping tabs and
/// pending tabs show placeholders, only active tabs materialize, and document windows stay
/// unmaterialized until phase 6. Geometry is asserted in window coordinates.
/// </summary>
public class DockAreaLayoutTests
{
    private const double SplitterThickness = 4;

    private static TabGroupNode Group(string? active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    private static SplitChild Child(TabTreeNode node, double share) => new(node, share);

    private static SplitNode Row(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Row, Children = [.. children] };

    private static SplitNode Column(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Column, Children = [.. children] };

    private static LayoutState DockState(TabTreeNode root, string? current) =>
        LayoutState.Empty with { DockArea = new DockAreaState { Root = root, CurrentTabId = current } };

    /// <summary>Registry + coordinator with dock content claimed by the "d" prefix (spec TW-9.11).</summary>
    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle, CountingTabFactory Docs) DockSetup(
        Func<string, object>? create = null)
    {
        var registry = new ToolWindowRegistry();
        var docs = new CountingTabFactory("d", create);
        registry.RegisterDockContent(docs);
        return (registry, new ContentLifecycle(registry), docs);
    }

    [AvaloniaFact]
    public void DA_2_1_split_shares_map_to_rendered_sizes()
    {
        var state = DockState(Row(Child(Group("d1", "d1"), 0.25), Child(Group("d2", "d2"), 0.75)), "d1");

        var window = Show(state, new ToolWindowRegistry());

        var total = Part(window, "PART_DockArea").Bounds.Width - SplitterThickness;
        var host1 = BoundsIn(TabHost(window, "d1"), window);
        var host2 = BoundsIn(TabHost(window, "d2"), window);
        Assert.Equal(0.25 * total, host1.Width, 1.0);
        Assert.Equal(0.75 * total, host2.Width, 1.0);
        Assert.True(host1.Right <= host2.X + 1);
    }

    [AvaloniaFact]
    public void DA_2_1_nested_split_renders_perpendicular()
    {
        var state = DockState(
            Row(
                Child(Group("d1", "d1"), 0.5),
                Child(Column(Child(Group("d2", "d2"), 0.5), Child(Group("d3", "d3"), 0.5)), 0.5)),
            "d1");

        var window = Show(state, new ToolWindowRegistry());

        var host1 = BoundsIn(TabHost(window, "d1"), window);
        var host2 = BoundsIn(TabHost(window, "d2"), window);
        var host3 = BoundsIn(TabHost(window, "d3"), window);
        Assert.True(host1.Right <= host2.X + 1); // колонка справа от d1
        Assert.Equal(host2.X, host3.X, 1.0);
        Assert.True(host2.Bottom <= host3.Y + 1); // d2 над d3 (Column)
    }

    [AvaloniaFact]
    public void DA_2_2_DA_E24_render_clamps_the_minimum_without_touching_the_state()
    {
        var state = DockState(Row(Child(Group("d1", "d1"), 0.02), Child(Group("d2", "d2"), 0.98)), "d1");

        var window = Show(state, new ToolWindowRegistry());

        // Доля дала бы ~15 px — рендер клемпит к минимуму слоя UI (DA-2.2), состояние цело.
        Assert.True(TabHost(window, "d1").Bounds.Width >= 44);
        var workspace = Assert.IsType<BerthWorkspace>(window.Content);
        Assert.Same(state, workspace.State);
    }

    [AvaloniaFact]
    public void DA_9_4_sleeping_active_tab_shows_a_placeholder()
    {
        var (registry, lifecycle, docs) = DockSetup();
        var state = DockState(Group("x1", "x1"), "x1"); // владелец x1 не заявлен — спит

        var window = Show(state, registry, lifecycle: lifecycle);

        var placeholder = Assert.IsType<TextBlock>(TabHost(window, "x1").Child);
        Assert.Equal("x1", placeholder.Text);
        Assert.Equal(0, docs.Created);
    }

    [AvaloniaFact]
    public void DA_9_6_titles_come_from_the_provider_with_the_id_fallback()
    {
        var state = DockState(Group("x1", "d1", "x1"), null) with
        {
            DockArea = new DockAreaState { Root = Group("x1", "d1", "x1"), CurrentTabId = "x1" },
        };

        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = new BerthWorkspace
            {
                State = state,
                Registry = new ToolWindowRegistry(),
                TabTitleProvider = id => string.Equals(id, "d1", StringComparison.Ordinal) ? "Doc One" : null,
            },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            "Doc One",
            TabHeader(window, "d1").GetVisualDescendants().OfType<TextBlock>().First().Text);
        Assert.Equal(
            "x1",
            TabHeader(window, "x1").GetVisualDescendants().OfType<TextBlock>().First().Text);
        // Заглушка спящей активной вкладки тоже берёт заголовок у провайдера (фолбэк — id).
        Assert.Equal("x1", Assert.IsType<TextBlock>(TabHost(window, "x1").Child).Text);
    }

    [AvaloniaFact]
    public void Document_windows_are_not_materialized_until_phase_6()
    {
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(0, 0, 300, 200), Group("d2", "d2"), "d2"),
                ],
                ActiveDockHost = DockHost.DocumentWindow(0),
            },
        };

        var window = Show(state, new ToolWindowRegistry());

        Assert.Equal(["d1"], TabHosts(window).Select(h => h.TabId));
    }

    [AvaloniaFact]
    public void Empty_dock_area_renders_no_tabs()
    {
        var window = Show(LayoutState.Empty, new ToolWindowRegistry());

        Assert.Empty(TabHosts(window));
        Assert.False(Part(window, "PART_TabStrip").IsVisible); // пустая корневая группа без полосы
    }

    [AvaloniaFact]
    public void DA_9_6_only_active_tabs_materialize()
    {
        var (registry, lifecycle, docs) = DockSetup();
        var state = DockState(Group("d1", "d1", "d2"), "d1");

        var window = Show(state, registry, lifecycle: lifecycle);

        Assert.Equal(1, docs.Created); // лениво: только активная вкладка группы (TW-9.3)
        Assert.Equal(["d1"], TabHosts(window).Select(h => h.TabId)); // хост d2 ещё не существует
    }

    [AvaloniaFact]
    public void DA_6_2_active_tab_and_active_document_pseudo_classes()
    {
        var state = DockState(Group("d1", "d1", "d2"), "d1");
        var window = Show(state, new ToolWindowRegistry());

        var header1 = TabHeader(window, "d1");
        var header2 = TabHeader(window, "d2");
        Assert.Contains(":active", header1.Classes);
        Assert.Contains(":current", header1.Classes); // активный документ (DA-6.2)
        Assert.DoesNotContain(":active", header2.Classes);
        Assert.DoesNotContain(":current", header2.Classes);

        // При активной панели акцент активного документа снят — :active группы остаётся.
        var withPanel = state with
        {
            ToolWindows = [Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
            ActiveToolWindowId = "p",
        };
        var second = Show(withPanel, Registry("p"));
        Assert.Contains(":active", TabHeader(second, "d1").Classes);
        Assert.DoesNotContain(":current", TabHeader(second, "d1").Classes);
    }
}
