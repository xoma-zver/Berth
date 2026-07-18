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
/// Inter-window drag on the windowed platform (task 6.2; spec TW-5.17, TW-7.8, DA-9.7):
/// sources in floating windows, targets across every window of the workspace with the
/// top-window hit-test, the float take-out of a release outside every target, the docking
/// composition of a floating panel dropped onto a stripe, and the no-trace cancellation from
/// a foreign window. Gesture coordinates compose Window.Position manually in headless (the
/// GestureSpace fallback — see TestAppBuilder); input is driven per-window, with the capture
/// owner receiving moves and the release at any coordinates (probe, task 6.2).
/// </summary>
public class CrossWindowDragTests
{
    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static ToolWindowState Panel(Window window, string id) =>
        St(window).ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static Window? FloatingWindowOf(Window main, string toolWindowId) =>
        TopLevel.GetTopLevel(Workspace(main).GetHost(toolWindowId)) is Window w && !ReferenceEquals(w, main)
            ? w
            : null;

    private static Window? DocumentWindowOf(Window main, string tabId) =>
        TopLevel.GetTopLevel(Workspace(main).TabHosts.GetHost(tabId)) is Window w && !ReferenceEquals(w, main)
            ? w
            : null;

    private static TabGroupNode Group(string? active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    /// <summary>Gesture (screen) point of a local point of the given window — the headless fallback math.</summary>
    private static Point Gesture(Window window, Point local) =>
        new(window.Position.X + local.X, window.Position.Y + local.Y);

    /// <summary>The gesture point in the local coordinates of the window whose input is driven.</summary>
    private static Point Local(Window window, Point gesture) =>
        new(gesture.X - window.Position.X, gesture.Y - window.Position.Y);

    /// <summary>Presses, crosses the threshold towards the target and releases — all driven on <paramref name="source"/>.</summary>
    private static void DragAndDrop(Window source, Point fromLocal, Point toLocal)
    {
        source.MouseDown(fromLocal, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        source.MouseMove(toLocal);
        Dispatcher.UIThread.RunJobs();
        source.MouseUp(toLocal, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Registry with dock content claiming the "d" prefix plus a panel with a "t"-claimed tab factory.</summary>
    private static ToolWindowRegistry DockRegistry(string? panelId = null, string? tabPrefix = null)
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new CountingTabFactory("d"));
        if (panelId is not null)
        {
            registry.Register(new ToolWindowDescriptor(
                panelId, panelId, new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
            {
                TabFactory = new CountingTabFactory(tabPrefix!),
            });
        }

        return registry;
    }

    // ---- sources in floating windows (TW-5.17, DA-9.7, task 6.2) ----

    [AvaloniaFact]
    public void DA_9_7_tab_drag_from_a_floating_panel_lands_in_the_main_dock_area()
    {
        var registry = DockRegistry("p", "t");
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState { Root = Group("d1", "d1"), CurrentTabId = "d1" },
            ToolWindows =
            [
                Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    Mode = ToolWindowMode.Float,
                    IsOpen = true,
                    FloatingBounds = new FloatingBounds(900, 50, 300, 220),
                    ContentTree = Group("t1", "t1", "t2"),
                },
            ],
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var floating = FloatingWindowOf(main, "p")!;
        var host = Workspace(main).TabHosts.GetHost("t2");

        // The source header lives in the floating window; the target is the center zone of
        // the main group, clear of its edge wedges.
        var start = Center(TabHeader(floating, "t2"), floating);
        var groupView = (Control)TabHost(main, "d1").GetVisualParent()!.GetVisualParent()!;
        var rect = BoundsIn(groupView, main);
        var target = Gesture(main, new Point(
            rect.Right - (rect.Width * 0.3), rect.Bottom - (rect.Height * 0.3)));
        DragAndDrop(floating, start, Local(floating, target));

        // The cross-host move landed with activation and focus (DA-5.4, DA-6.4); the same
        // cached host reattached into the main window (DA-9.6).
        Assert.Equal(["d1", "t2"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
        Assert.Equal("t2", St(main).DockArea.CurrentTabId);
        Assert.Null(St(main).ActiveToolWindowId); // the cross-host ActivateTab cleared it (TW-6.5)
        Assert.Equal(["t1"], Assert.IsType<TabGroupNode>(Panel(main, "p").ContentTree).Tabs);
        Assert.Same(host, Workspace(main).TabHosts.GetHost("t2"));
        Assert.Same(main, TopLevel.GetTopLevel(host));
        Assert.Empty(LayoutInvariants.Validate(St(main), registry));
    }

    [AvaloniaFact]
    public void DA_9_7_tab_drag_from_the_main_window_into_a_document_window_strip()
    {
        var registry = DockRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1", "d2"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(900, 50, 400, 300), Group("d9", "d9"), "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var docWindow = DocumentWindowOf(main, "d9")!;
        var host = Workspace(main).TabHosts.GetHost("d2");

        // Mid-gesture the target visuals paint in the document window's own overlay (task
        // 6.2) — over a strip that is the reorder preview's placeholder (stage 2, v0.18).
        var start = Center(TabHeader(main, "d2"), main);
        var after = BoundsIn(TabHeader(docWindow, "d9"), docWindow);
        var target = Gesture(docWindow, new Point(after.Right + 10, after.Center.Y));
        main.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        main.MouseMove(Local(main, target));
        Dispatcher.UIThread.RunJobs();
        var markers = ((FloatingWindowLayer.FloatingWindowBase)docWindow).Markers;
        Assert.True(Part(markers, "PART_StripPlaceholder").IsVisible);
        main.MouseUp(Local(main, target), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // One MoveTab into the receiving group, then the cross-host ActivateTab (DA-9.7).
        var group = Assert.IsType<TabGroupNode>(St(main).DockArea.Windows[0].Root);
        Assert.Equal(["d9", "d2"], group.Tabs);
        Assert.Equal("d2", St(main).DockArea.Windows[0].CurrentTabId);
        Assert.Equal(DockHost.DocumentWindow(0), St(main).DockArea.ActiveDockHost);
        Assert.Equal(["d1"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
        Assert.Same(host, Workspace(main).TabHosts.GetHost("d2"));
        Assert.Same(docWindow, TopLevel.GetTopLevel(host)); // the same host crossed the windows
    }

    [AvaloniaFact]
    public void TW_5_17_document_window_over_the_main_group_wins_the_drop()
    {
        // The document window overlaps the main group's center: a newly shown floating
        // window is on top of the MRU order, so its center zone wins over the occluded one.
        var registry = DockRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1", "d2"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(300, 150, 350, 250), Group("d9", "d9"), "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var docWindow = DocumentWindowOf(main, "d9")!;

        var start = Center(TabHeader(main, "d1"), main);
        var docGroup = (Control)TabHost(docWindow, "d9").GetVisualParent()!.GetVisualParent()!;
        var target = Gesture(docWindow, BoundsIn(docGroup, docWindow).Center);
        DragAndDrop(main, start, Local(main, target));

        // The drop joined the document window's group — not the main group underneath.
        Assert.Equal(["d9", "d1"], Assert.IsType<TabGroupNode>(St(main).DockArea.Windows[0].Root).Tabs);
        Assert.Equal(["d2"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
        Assert.Equal(DockHost.DocumentWindow(0), St(main).DockArea.ActiveDockHost);
    }

    // ---- the float take-out (TW-7.8) ----

    [AvaloniaFact]
    public void TW_7_8_header_release_outside_targets_floats_the_open_panel_at_the_point()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var main = Show(state, registry);
        var host = Workspace(main).GetHost("a");
        var hostSize = host.Bounds.Size;

        var header = Part(Decorator(main, "a"), "PART_Header");
        var start = new Point(BoundsIn(header, main).X + 40, BoundsIn(header, main).Center.Y);
        DragAndDrop(main, start, new Point(400, 300)); // the dock area — outside every target

        // The take-out of TW-7.8: Float at the release point, the hosted content's size,
        // activated with focus; the same host reattached into the new OS window (TW-9.13).
        var a = Panel(main, "a");
        Assert.Equal(ToolWindowMode.Float, a.Mode);
        Assert.True(a.IsOpen);
        Assert.Equal(new FloatingBounds(400, 300, hostSize.Width, hostSize.Height), a.FloatingBounds);
        Assert.Equal("a", St(main).ActiveToolWindowId);
        var floating = FloatingWindowOf(main, "a");
        Assert.NotNull(floating);
        Assert.Equal(new PixelPoint(400, 300), floating.Position);
        Assert.Same(host, Workspace(main).GetHost("a"));
    }

    [AvaloniaFact]
    public void TW_7_8_release_outside_for_an_already_floating_panel_is_identity()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    Mode = ToolWindowMode.Float,
                    IsOpen = true,
                    FloatingBounds = new FloatingBounds(900, 50, 300, 200),
                },
            ],
        };
        var main = Show(state, registry);
        var floating = FloatingWindowOf(main, "a")!;
        var before = St(main);

        // The decorator header inside the floating window is a drag source (task 6.2);
        // releasing it outside every target changes nothing — moving the window is the
        // window's own gesture (TW-7.8).
        var header = Part(Decorator(floating, "a"), "PART_Header");
        var start = new Point(BoundsIn(header, floating).X + 40, BoundsIn(header, floating).Center.Y);
        DragAndDrop(floating, start, Local(floating, new Point(2000, 800)));

        Assert.Same(before, St(main)); // no command, no activation — an identity (TW-7.8)
    }

    [AvaloniaFact]
    public void DA_9_7_tab_release_outside_every_window_opens_a_document_window_at_the_point()
    {
        var registry = DockRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState { Root = Group("d1", "d1", "d2"), CurrentTabId = "d1" },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var groupView = (Control)TabHost(main, "d1").GetVisualParent()!.GetVisualParent()!;
        var donorSize = groupView.Bounds.Size;
        var host = Workspace(main).TabHosts.GetHost("d2");

        var start = Center(TabHeader(main, "d2"), main);
        DragAndDrop(main, start, new Point(1200, 700)); // outside the 800×600 main window

        // The outside take-out of DA-9.7: a new document window at the point, sized by the
        // donor group, activated with focus.
        var docWindow = Assert.Single(St(main).DockArea.Windows);
        Assert.Equal(["d2"], Assert.IsType<TabGroupNode>(docWindow.Root).Tabs);
        Assert.Equal(new FloatingBounds(1200, 700, donorSize.Width, donorSize.Height), docWindow.Bounds);
        Assert.Equal(DockHost.DocumentWindow(0), St(main).DockArea.ActiveDockHost);
        Assert.Equal(["d1"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
        Assert.Same(host, Workspace(main).TabHosts.GetHost("d2"));
        Assert.Same(DocumentWindowOf(main, "d2"), TopLevel.GetTopLevel(host));
    }

    // ---- docking a floating panel by drop (TW-5.17, TW-7.8) ----

    [AvaloniaFact]
    public void TW_5_17_floating_panel_header_dropped_on_a_stripe_docks_it()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    Mode = ToolWindowMode.Float,
                    LastInternalMode = ToolWindowMode.DockUnpinned,
                    IsOpen = true,
                    FloatingBounds = new FloatingBounds(900, 50, 300, 200),
                },
            ],
        };
        var main = Show(state, registry);
        var floating = FloatingWindowOf(main, "a")!;

        var header = Part(Decorator(floating, "a"), "PART_Header");
        var start = new Point(BoundsIn(header, floating).X + 40, BoundsIn(header, floating).Center.Y);
        var stripe = BoundsIn(Part(main, "PART_RightStripe"), main);
        var target = Gesture(main, new Point(stripe.Center.X, stripe.Top + 5));
        DragAndDrop(floating, start, Local(floating, target));

        // The docking composition (TW-7.8): Move into the zone's slot, then SetMode back to
        // the last internal mode — the floating window is gone.
        var a = Panel(main, "a");
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary), a.Slot);
        Assert.Equal(ToolWindowMode.DockUnpinned, a.Mode); // the way back of E27
        Assert.True(a.IsOpen);
        Assert.Null(FloatingWindowOf(main, "a"));
        Assert.Same(main, TopLevel.GetTopLevel(Workspace(main).GetHost("a")));
    }

    [AvaloniaFact]
    public void TW_5_17_window_mode_icon_dropped_on_its_own_stripe_position_still_docks()
    {
        // The same-position drop of a floating-mode icon is not an identity: the stripe drop
        // is a docking gesture (TW-7.8) — Move is a no-op, SetMode docks.
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    Mode = ToolWindowMode.Window,
                    IsOpen = true,
                    FloatingBounds = new FloatingBounds(900, 50, 300, 200),
                },
            ],
        };
        var main = Show(state, registry);
        Assert.NotNull(FloatingWindowOf(main, "a"));

        var own = BoundsIn(Button(main, "a"), main);
        DragAndDrop(main, own.Center, new Point(own.Center.X, own.Bottom + 2)); // the gap after itself

        var a = Panel(main, "a");
        Assert.Equal(ToolWindowMode.DockPinned, a.Mode); // the default LastInternalMode
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary), a.Slot);
        Assert.True(a.IsOpen);
        Assert.Null(FloatingWindowOf(main, "a"));
    }

    // ---- cancellation and external changes across windows (TW-5.17) ----

    [AvaloniaFact]
    public void TW_5_17_escape_cancels_a_drag_from_a_document_window_with_no_trace()
    {
        var registry = DockRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(900, 50, 400, 300), Group("d9", "d8", "d9"), "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var docWindow = DocumentWindowOf(main, "d9")!;
        var before = St(main);
        var drag = Workspace(main).Drag!;

        var start = Center(TabHeader(docWindow, "d8"), docWindow);
        docWindow.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        docWindow.MouseMove(Local(docWindow, new Point(400, 300))); // over the main window
        Dispatcher.UIThread.RunJobs();
        Assert.True(drag.GhostVisible);

        docWindow.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); // Esc in the source window
        Dispatcher.UIThread.RunJobs();
        Assert.False(drag.GhostVisible);
        docWindow.MouseUp(Local(docWindow, new Point(400, 300)), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(before, St(main)); // no command, no activation, no focus (DA-E22)
    }

    [AvaloniaFact]
    public void TW_5_17_external_change_mid_cross_window_drag_rebuilds_the_targets()
    {
        var registry = DockRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1", "d2"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(900, 50, 400, 300), Group("d9", "d9"), "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var docWindow = DocumentWindowOf(main, "d9")!;

        var start = Center(TabHeader(docWindow, "d9"), docWindow);
        docWindow.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        docWindow.MouseMove(Local(docWindow, new Point(400, 300)));
        Dispatcher.UIThread.RunJobs();

        // An external change mid-gesture: d2 closes, the main strip re-projects (TW-5.17).
        Workspace(main).State = St(main).CloseTab("d2");
        Dispatcher.UIThread.RunJobs();

        // The gesture continues over the updated geometry: drop after d1's header.
        var after = BoundsIn(TabHeader(main, "d1"), main);
        var target = Gesture(main, new Point(after.Right + 10, after.Center.Y));
        docWindow.MouseMove(Local(docWindow, target));
        Dispatcher.UIThread.RunJobs();
        docWindow.MouseUp(Local(docWindow, target), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["d1", "d9"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
        Assert.Empty(St(main).DockArea.Windows); // the emptied source window is gone (INV-D6)
        Assert.Equal("d9", St(main).DockArea.CurrentTabId);
        Assert.True(St(main).DockArea.ActiveDockHost.IsMainWindow);
    }

    // ---- the strip reorder preview across windows (DA-9.7 v0.17, stage 2) ----

    [AvaloniaFact]
    public void DA_9_7_strip_preview_reaches_a_document_window_and_collapses_the_main_donor()
    {
        var registry = DockRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1", "d2", "d3"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(900, 50, 400, 300), Group("d9", "d9"), "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var docWindow = DocumentWindowOf(main, "d9")!;
        var before = St(main);
        var drag = Workspace(main).Drag!;

        var d2 = BoundsIn(TabHeader(main, "d2"), main);
        var d9 = BoundsIn(TabHeader(docWindow, "d9"), docWindow);
        var target = Gesture(docWindow, new Point(d9.Right + 4, d9.Center.Y)); // the gap after d9
        main.MouseDown(d2.Center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        main.MouseMove(Local(main, target));
        Dispatcher.UIThread.RunJobs();

        // The insertion placeholder paints in the document window's own overlay, in its
        // local coordinates; the OS-window pointer chip keeps riding at the cursor (v0.18).
        Assert.True(drag.GhostVisible);
        var place = Part(docWindow, "PART_StripPlaceholder");
        Assert.True(place.IsVisible);
        Assert.Equal(d9.Right, Canvas.GetLeft(place), 1);
        Assert.Equal(d2.Width, place.Width, 1);

        // The donor strip back in the main window collapses the dragged header's place.
        Assert.Equal(0, TabHeader(main, "d2").Opacity);
        var shifted = Assert.IsType<TranslateTransform>(TabHeader(main, "d3").RenderTransform);
        Assert.Equal(-d2.Width, shifted.X, 1);

        // Cancellation restores both windows' natural projection with no trace (DA-E22).
        main.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(place.IsVisible);
        Assert.Equal(1, TabHeader(main, "d2").Opacity);
        Assert.Null(TabHeader(main, "d3").RenderTransform);
        main.MouseUp(Local(main, target), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(before, St(main));
    }

    [AvaloniaFact]
    public void DA_9_7_strip_to_strip_hover_across_windows_leaves_no_stale_placeholder()
    {
        var registry = DockRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1", "d2"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(900, 50, 400, 300), Group("d8", "d8"), "d8"),
                    new DocumentWindowState(new FloatingBounds(900, 500, 400, 300), Group("d9", "d9"), "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var windowA = DocumentWindowOf(main, "d8")!;
        var windowB = DocumentWindowOf(main, "d9")!;

        var d2 = BoundsIn(TabHeader(main, "d2"), main);
        var d8 = BoundsIn(TabHeader(windowA, "d8"), windowA);
        var d9 = BoundsIn(TabHeader(windowB, "d9"), windowB);
        var overA = Gesture(windowA, new Point(d8.Right + 4, d8.Center.Y));
        var overB = Gesture(windowB, new Point(d9.Right + 4, d9.Center.Y));
        main.MouseDown(d2.Center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        main.MouseMove(Local(main, overA));
        Dispatcher.UIThread.RunJobs();
        Assert.True(Part(windowA, "PART_StripPlaceholder").IsVisible);

        // Straight from window A's strip onto window B's, with no non-strip frame in
        // between: the previous window's placeholder hides with the move — the full-hide
        // rule the marker path already follows (DA-9.7 v0.18).
        main.MouseMove(Local(main, overB));
        Dispatcher.UIThread.RunJobs();
        Assert.False(Part(windowA, "PART_StripPlaceholder").IsVisible);
        Assert.True(Part(windowB, "PART_StripPlaceholder").IsVisible);

        // The cancelled gesture leaves nothing behind in any window (DA-E22).
        main.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Part(windowA, "PART_StripPlaceholder").IsVisible);
        Assert.False(Part(windowB, "PART_StripPlaceholder").IsVisible);
        main.MouseUp(Local(main, overB), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
