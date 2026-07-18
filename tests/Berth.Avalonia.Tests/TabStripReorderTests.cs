using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// The live strip reorder preview of tab drags — stage 2 of the rich drag visual language
/// (spec DA-9.7 v0.18): over a strip insertion zone the headers move apart around the framed
/// insertion placeholder sized off the source header at the gesture start — the place the
/// tab takes on release — while the pointer chip keeps riding at the cursor, the single
/// gesture language of every target kind; the dragged header's place collapses — in the
/// donor strip of a cross-strip hover too. Everything is a pure visual override of leaf
/// chrome (the section 12 contract of tool-windows): shifts are RenderTransforms and the
/// collapse is opacity, so layout Bounds — the hit-test zone geometry — never change, and
/// cancellation resets the overrides with no trace (DA-E22 by construction); an external
/// re-projection re-lays the overrides over the rebuilt headers without a pointer move. The
/// placeholder clips into the band — a narrow band's overflow stays unhandled
/// (document-area, section 11).
/// </summary>
public class TabStripReorderTests
{
    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static DragController Drag(Window window) => Workspace(window).Drag!;

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

    /// <summary>Presses at <paramref name="from"/> and moves past the threshold to <paramref name="to"/> without releasing.</summary>
    private static void DragTo(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
    }

    private static void MoveTo(Window window, Point to)
    {
        window.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Release(Window window, Point at)
    {
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Ends a visuals-only gesture without a command: cancel, then release (TW-5.17).</summary>
    private static void CancelAndRelease(Window window, Point at)
    {
        PressEscape(window);
        Release(window, at);
    }

    /// <summary>The reorder shift of a header: the X of its translate override, 0 without one.</summary>
    private static double OffsetX(Control header) =>
        header.RenderTransform is TranslateTransform transform ? transform.X : 0;

    /// <summary>The strip band (PART_TabStrip bar) hosting the given tab's header.</summary>
    private static Control BandOf(Window window, string tabId)
    {
        for (Visual? node = TabHeader(window, tabId); node is not null; node = node.GetVisualParent())
        {
            if (node is Control control
                && string.Equals(control.Name, "PART_TabStrip", StringComparison.Ordinal))
            {
                return control;
            }
        }

        throw new InvalidOperationException($"No strip band hosts tab '{tabId}'.");
    }

    // ---- the preview language over a strip (DA-9.7 v0.18) ----

    [AvaloniaFact]
    public void DA_9_7_strip_hover_parts_the_headers_around_the_placeholder()
    {
        var window = Show(DockState(Group("d1", "d1", "d2", "d3"), "d1"), DockRegistry());
        var d1 = BoundsIn(TabHeader(window, "d1"), window);
        var d3 = BoundsIn(TabHeader(window, "d3"), window);
        var d1Layout = TabHeader(window, "d1").Bounds;

        // Drag d3 to the front zone: before the first header's midpoint.
        var target = new Point(d1.Left + 1, d1.Center.Y);
        DragTo(window, d3.Center, target);

        // The pointer chip stays at the cursor — the single gesture language of every
        // target (v0.18) — while the stage-1 insertion line yields to the preview.
        Assert.True(Drag(window).GhostVisible);
        Assert.False(Drag(window).GhostShowsMiniature);
        Assert.False(Part(window, "PART_DropMarker").IsVisible);

        // The placeholder frames the front position at the source header's width...
        var place = Part(window, "PART_StripPlaceholder");
        Assert.True(place.IsVisible);
        Assert.Equal(d1.Left, Canvas.GetLeft(place), 1);
        Assert.Equal(d3.Width, place.Width, 1);

        // ...the headers move apart by exactly that width — RenderTransform only, layout
        // Bounds (the hit-test zone geometry) stay untouched...
        Assert.Equal(d3.Width, OffsetX(TabHeader(window, "d1")), 1);
        Assert.Equal(d3.Width, OffsetX(TabHeader(window, "d2")), 1);
        Assert.Equal(d1Layout, TabHeader(window, "d1").Bounds);

        // ...and the dragged header's own place collapses.
        Assert.Equal(0, TabHeader(window, "d3").Opacity);

        CancelAndRelease(window, target);
    }

    [AvaloniaFact]
    public void DA_E40_identity_gap_previews_the_tab_in_place()
    {
        var window = Show(DockState(Group("d1", "d1", "d2", "d3"), "d1"), DockRegistry());
        var d2 = BoundsIn(TabHeader(window, "d2"), window);

        // The gap right after itself lays out identically to the natural order: the
        // placeholder takes the dragged header's own place and nobody moves — the preview
        // of the identity drop (DA-E40).
        var target = new Point(d2.Right + 2, d2.Center.Y);
        DragTo(window, d2.Center, target);

        var place = Part(window, "PART_StripPlaceholder");
        Assert.True(place.IsVisible);
        Assert.Equal(d2.Left, Canvas.GetLeft(place), 1);
        Assert.Equal(d2.Width, place.Width, 1);
        Assert.Null(TabHeader(window, "d1").RenderTransform);
        Assert.Null(TabHeader(window, "d3").RenderTransform);
        Assert.Equal(0, TabHeader(window, "d2").Opacity);

        CancelAndRelease(window, target);
    }

    [AvaloniaFact]
    public void DA_9_7_cross_strip_hover_collapses_the_donor_strip()
    {
        var root = RowSplit(
            Child(Group("d1", "d1", "d2"), 0.5),
            Child(Group("d3", "d3"), 0.5));
        var window = Show(DockState(root, "d1"), DockRegistry());
        var d1 = BoundsIn(TabHeader(window, "d1"), window);
        var d3 = BoundsIn(TabHeader(window, "d3"), window);

        // Drag d1 into the second group's strip, after d3.
        var target = new Point(d3.Right + 4, d3.Center.Y);
        DragTo(window, d1.Center, target);

        // The receiver opens the gap after d3 at the source header's width...
        var place = Part(window, "PART_StripPlaceholder");
        Assert.True(place.IsVisible);
        Assert.Equal(d3.Right, Canvas.GetLeft(place), 1);
        Assert.Equal(d1.Width, place.Width, 1);
        Assert.Null(TabHeader(window, "d3").RenderTransform);

        // ...and the donor strip collapses the dragged header's place too (v0.18).
        Assert.Equal(0, TabHeader(window, "d1").Opacity);
        Assert.Equal(-d1.Width, OffsetX(TabHeader(window, "d2")), 1);

        CancelAndRelease(window, target);
    }

    [AvaloniaFact]
    public void TW_9_5_header_row_strip_of_a_panel_previews_too()
    {
        var registry = DockRegistry();
        registry.Register(new ToolWindowDescriptor(
            "p", "p", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            TabFactory = new CountingTabFactory("t"),
        });
        var state = DockState(Group("d1", "d1"), "d1") with
        {
            ToolWindows =
            [
                Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    ContentTree = Group("t1", "t1", "t2"),
                },
            ],
        };
        var window = Show(state, registry);
        var t1 = BoundsIn(TabHeader(window, "t1"), window);
        var t2 = BoundsIn(TabHeader(window, "t2"), window);

        // The strip of a panel root group lives in the decorator header row (TW-9.5) and
        // previews like any strip: drag t2 before t1.
        var target = new Point(t1.Left + 1, t1.Center.Y);
        DragTo(window, t2.Center, target);

        var place = Part(window, "PART_StripPlaceholder");
        Assert.True(place.IsVisible);
        Assert.Equal(t1.Left, Canvas.GetLeft(place), 1);
        Assert.Equal(t2.Width, place.Width, 1);
        Assert.Equal(t2.Width, OffsetX(TabHeader(window, "t1")), 1);
        Assert.Equal(0, TabHeader(window, "t2").Opacity);

        CancelAndRelease(window, target);
    }

    // ---- leaving, cancelling, re-projection ----

    [AvaloniaFact]
    public void DA_9_7_leaving_the_strip_restores_the_headers()
    {
        var window = Show(DockState(Group("d1", "d1", "d2", "d3"), "d1"), DockRegistry());
        var d1 = BoundsIn(TabHeader(window, "d1"), window);
        var d3 = BoundsIn(TabHeader(window, "d3"), window);

        DragTo(window, d3.Center, new Point(d1.Left + 1, d1.Center.Y));
        Assert.True(Part(window, "PART_StripPlaceholder").IsVisible);

        // Off the strip, onto the group center: every override resets and the stage-1
        // marker language takes over; the pointer chip rode along the whole way.
        var center = BoundsIn(TabHost(window, "d1"), window).Center;
        MoveTo(window, center);

        Assert.False(Part(window, "PART_StripPlaceholder").IsVisible);
        Assert.True(Drag(window).GhostVisible);
        Assert.True(Part(window, "PART_DropMarker").IsVisible);
        Assert.Null(TabHeader(window, "d1").RenderTransform);
        Assert.Null(TabHeader(window, "d2").RenderTransform);
        Assert.Equal(1, TabHeader(window, "d3").Opacity);

        CancelAndRelease(window, center);
    }

    [AvaloniaFact]
    public void DA_E22_cancelled_strip_hover_leaves_no_trace()
    {
        var window = Show(DockState(Group("d1", "d1", "d2", "d3"), "d1"), DockRegistry());
        var before = St(window);
        var d1 = BoundsIn(TabHeader(window, "d1"), window);
        var d3 = BoundsIn(TabHeader(window, "d3"), window);
        var target = new Point(d1.Left + 1, d1.Center.Y);
        DragTo(window, d3.Center, target);
        Assert.Equal(0, TabHeader(window, "d3").Opacity);

        PressEscape(window);

        // The overrides reset with the cancellation — the real headers were never
        // reordered, so there is nothing to undo (DA-E22 by construction).
        Assert.False(Part(window, "PART_StripPlaceholder").IsVisible);
        Assert.Null(TabHeader(window, "d1").RenderTransform);
        Assert.Null(TabHeader(window, "d2").RenderTransform);
        Assert.Equal(1, TabHeader(window, "d3").Opacity);

        Release(window, target);
        Assert.Same(before, St(window)); // no command, no activation, no focus
    }

    [AvaloniaFact]
    public void DA_9_7_external_reprojection_reapplies_the_overrides()
    {
        var window = Show(DockState(Group("d1", "d1", "d2", "d3"), "d1"), DockRegistry());
        var d1 = BoundsIn(TabHeader(window, "d1"), window);
        var d3 = BoundsIn(TabHeader(window, "d3"), window);
        var target = new Point(d1.Left + 1, d1.Center.Y);
        DragTo(window, d3.Center, target);
        var oldHeader = TabHeader(window, "d1");
        Assert.Equal(d3.Width, OffsetX(oldHeader), 1);

        // An external state change re-projects the workspace: the strip headers are leaf
        // chrome and rebuild, and the overrides re-lay over the fresh views without a
        // pointer move (the section 12 contract of tool-windows).
        Workspace(window).State = St(window).SetSideSize(ToolWindowSide.Left, 0.4);
        Dispatcher.UIThread.RunJobs();

        var newHeader = TabHeader(window, "d1");
        Assert.NotSame(oldHeader, newHeader);
        Assert.Equal(d3.Width, OffsetX(newHeader), 1);
        Assert.Equal(0, TabHeader(window, "d3").Opacity);
        Assert.True(Part(window, "PART_StripPlaceholder").IsVisible);

        CancelAndRelease(window, target);
    }

    // ---- clipping of a narrow band (document-area, section 11) ----

    [AvaloniaFact]
    public void DA_9_7_placeholder_clips_into_the_band()
    {
        // Ten long-titled tabs overflow the right group's strip; the foreign tab dragged in
        // near the band's right edge would land beyond it, so the placeholder clips into
        // the band — overflow itself stays unhandled (document-area, section 11). The
        // r-tabs are unclaimed and sleep; dock-area strips take any tab regardless.
        var right = Group("r1", [.. Enumerable.Range(1, 10).Select(i => $"r{i}")]);
        var root = RowSplit(Child(Group("d1", "d1"), 0.5), Child(right, 0.5));
        var window = Show(DockState(root, "d1"), DockRegistry());
        Workspace(window).TabTitleProvider = id => $"{id} — a rather long title";
        Dispatcher.UIThread.RunJobs();

        var draggedWidth = BoundsIn(TabHeader(window, "d1"), window).Width;
        var band = BoundsIn(BandOf(window, "r1"), window);
        var target = new Point(band.Right - 6, band.Center.Y);
        DragTo(window, Center(TabHeader(window, "d1"), window), target);

        Assert.True(Drag(window).GhostVisible); // the chip rides at the cursor (v0.18)
        Assert.Equal(0, TabHeader(window, "d1").Opacity); // the donor collapse holds

        // The placeholder paints only inside the band: clipped at the edge, or gone
        // entirely when the gap lies wholly beyond it.
        var place = Part(window, "PART_StripPlaceholder");
        if (place.IsVisible)
        {
            Assert.True(Canvas.GetLeft(place) + place.Width <= band.Right + 0.5);
            Assert.True(place.Width < draggedWidth);
        }

        CancelAndRelease(window, target);
    }
}
