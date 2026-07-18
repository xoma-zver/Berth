using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// The floating layer on a platform with real windows (task 6.0): Float/Window tool windows
/// as OS windows (spec TW-7.1…TW-7.5), document windows (DA-7.1…DA-7.3, DA-7.6), the layer
/// reattachment of TW-9.13, window gestures reduced to core commands (ADR-0004) and the
/// multi-window focus wiring (TW-6.1, TW-6.6, DA-6.4). Floating OS windows are outside the
/// main window's visual tree, so tests reach them through the internal host caches.
/// </summary>
public class FloatingWindowTests
{
    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static ToolWindowState Panel(Window window, string id) =>
        St(window).ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    /// <summary>OS window hosting the tool window's decorator, or null while it is not floating.</summary>
    private static Window? FloatingWindowOf(Window main, string toolWindowId) =>
        TopLevel.GetTopLevel(Workspace(main).GetHost(toolWindowId)) is Window w && !ReferenceEquals(w, main)
            ? w
            : null;

    /// <summary>OS window hosting the tab, or null while the tab is not in a materialized document window.</summary>
    private static Window? DocumentWindowOf(Window main, string tabId) =>
        TopLevel.GetTopLevel(Workspace(main).TabHosts.GetHost(tabId)) is Window w && !ReferenceEquals(w, main)
            ? w
            : null;

    private sealed class StateChangeCounter
    {
        public StateChangeCounter(Window main)
        {
            Workspace(main).PropertyChanged += (_, e) =>
            {
                if (e.Property == BerthWorkspace.StateProperty)
                {
                    Count++;
                }
            };
        }

        public int Count { get; private set; }
    }

    private sealed class CountingBodyFactory(Func<string, object> create) : IToolWindowContentFactory
    {
        public int Created { get; private set; }

        public int Released { get; private set; }

        public object CreateContent(string toolWindowId)
        {
            Created++;
            return create(toolWindowId);
        }

        public void ReleaseContent(string toolWindowId, object content) => Released++;
    }

    /// <summary>One tool window «a» in the given mode, open, with a TextBox body.</summary>
    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle, LayoutState State,
        TextBox Body, CountingBodyFactory Factory) Setup(
        ToolWindowMode mode,
        FloatingBounds? bounds = null,
        ContentRetentionPolicy retention = ContentRetentionPolicy.KeepWhileRegistered)
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var body = new TextBox();
        var factory = new CountingBodyFactory(_ => body);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "a", "Alpha", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            RetentionPolicy = retention,
            ContentFactory = factory,
        });
        state = state with
        {
            ToolWindows =
            [
                .. state.ToolWindows.Select(w => w with
                {
                    Mode = mode,
                    LastInternalMode = mode.IsInternal() ? mode : ToolWindowMode.DockPinned,
                    IsOpen = true,
                    FloatingBounds = bounds,
                }),
            ],
        };
        return (registry, lifecycle, state, body, factory);
    }

    private static TabGroupNode Group(string? active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    /// <summary>Dock content claimed by the "d" prefix, TextBox per tab.</summary>
    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle,
        Dictionary<string, TextBox> Boxes, CountingTabFactory Factory) DockSetup()
    {
        var registry = new ToolWindowRegistry();
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var factory = new CountingTabFactory("d", id => boxes[id] = new TextBox());
        registry.RegisterDockContent(factory);
        return (registry, new ContentLifecycle(registry), boxes, factory);
    }

    private static MenuItem Item(IEnumerable<object?> items, string header) =>
        items.OfType<MenuItem>().First(i => string.Equals(i.Header as string, header, StringComparison.Ordinal));

    private static void Invoke(MenuItem item)
    {
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    // ---- TW-7.1, TW-7.2: the two window kinds ----

    [AvaloniaFact]
    public void TW_7_1_float_is_an_owned_window_outside_the_taskbar()
    {
        var (registry, lifecycle, state, body, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(40, 50, 320, 240));
        var main = Show(state, registry, lifecycle: lifecycle);

        var floating = FloatingWindowOf(main, "a");
        Assert.NotNull(floating);
        Assert.Same(main, floating.Owner); // owned: above the main window, minimizes with it
        Assert.False(floating.ShowInTaskbar);
        Assert.Equal(new PixelPoint(40, 50), floating.Position);
        Assert.Equal(new Size(320, 240), floating.ClientSize);
        Assert.Equal("Alpha", floating.Title);
        // The window hosts the same cached decorator with the materialized body (TW-9.13).
        Assert.Same(Decorator(floating, "a"), Workspace(main).GetHost("a"));
        Assert.Contains(body, floating.GetVisualDescendants());
    }

    [AvaloniaFact]
    public void TW_7_2_window_is_an_independent_top_level()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Window, new FloatingBounds(40, 50, 320, 240));
        var main = Show(state, registry, lifecycle: lifecycle);

        var floating = FloatingWindowOf(main, "a");
        Assert.NotNull(floating);
        Assert.Null(floating.Owner); // independent: own z-order and taskbar presence
        Assert.True(floating.ShowInTaskbar);
    }

    [AvaloniaFact]
    public void TW_5_6_float_to_window_rehosts_the_same_host_with_the_same_bounds()
    {
        var (registry, lifecycle, state, _, factory) = Setup(
            ToolWindowMode.Float, new FloatingBounds(30, 40, 300, 200));
        var main = Show(state, registry, lifecycle: lifecycle);
        var host = Workspace(main).GetHost("a");
        var floatWindow = FloatingWindowOf(main, "a");

        Workspace(main).State = St(main).SetMode("a", ToolWindowMode.Window);
        Dispatcher.UIThread.RunJobs();

        var windowed = FloatingWindowOf(main, "a");
        Assert.NotNull(windowed);
        Assert.NotSame(floatWindow, windowed); // the OS window is replaced…
        Assert.Same(host, Workspace(main).GetHost("a")); // …the decorator is not (TW-9.13)
        Assert.Same(host, Decorator(windowed, "a"));
        Assert.Equal(new PixelPoint(30, 40), windowed.Position); // same bounds (TW-5.6)
        Assert.Equal(new Size(300, 200), windowed.ClientSize);
        Assert.Null(windowed.Owner);
        Assert.Equal(1, factory.Created); // a re-host is no content transition (TW-9.2)
    }

    // ---- TW-7.3, E8: closing ----

    [AvaloniaFact]
    public void TW_7_3_system_close_is_one_close_command_keeping_the_mode()
    {
        var (registry, lifecycle, state, _, factory) = Setup(
            ToolWindowMode.Float, new FloatingBounds(40, 50, 320, 240),
            ContentRetentionPolicy.DisposeOnClose);
        var main = Show(state, registry, lifecycle: lifecycle);
        var floating = FloatingWindowOf(main, "a")!;
        var changes = new StateChangeCounter(main);

        floating.Close(); // the user's system close button
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, changes.Count); // exactly one Close command (TW-7.3, ADR-0004)
        var a = Panel(main, "a");
        Assert.False(a.IsOpen);
        Assert.Equal(ToolWindowMode.Float, a.Mode); // the mode survives: the next Open re-floats
        Assert.Null(FloatingWindowOf(main, "a"));
        Assert.Equal(1, factory.Released); // DisposeOnClose released exactly once (TW-9.2)
    }

    [AvaloniaFact]
    public void E8_hiding_the_icon_closes_the_floating_window_without_extra_commands()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Window, new FloatingBounds(40, 50, 320, 240));
        var main = Show(state, registry, lifecycle: lifecycle);
        Assert.NotNull(FloatingWindowOf(main, "a"));
        var changes = new StateChangeCounter(main);

        Workspace(main).State = St(main).SetIconVisible("a", false);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, changes.Count); // only the assignment: the projection close is no command
        Assert.Null(FloatingWindowOf(main, "a"));
        Assert.False(Panel(main, "a").IsOpen);
    }

    // ---- TW-7.5, DA-7.6: teardown ----

    [AvaloniaFact]
    public void TW_7_5_closing_the_main_window_tears_down_without_commands()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(40, 50, 320, 240));
        state = state with
        {
            DockArea = new DockAreaState
            {
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(60, 70, 300, 200), Group("x", "x"), "x"),
                ],
                ActiveDockHost = DockHost.DocumentWindow(0),
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var floating = FloatingWindowOf(main, "a")!;
        var document = DocumentWindowOf(main, "x")!;
        var before = St(main);

        main.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(floating.IsVisible);
        Assert.False(document.IsVisible);
        // Teardown is no command (TW-7.5, DA-7.6): the state still holds the open floating
        // windows for the next session — the same instance, untouched.
        Assert.Same(before, Workspace(main).State);
        Assert.True(before.ToolWindows[0].IsOpen);
        Assert.Single(before.DockArea.Windows);
    }

    // ---- TW-5.6, TW-5.9: bounds plumbing ----

    [AvaloniaFact]
    public void TW_5_6_menu_float_adopts_the_current_screen_bounds()
    {
        var (registry, lifecycle, state, _, _) = Setup(ToolWindowMode.DockPinned);
        var main = Show(state, registry, lifecycle: lifecycle);
        var host = Workspace(main).GetHost("a");
        var origin = host.PointToScreen(default);
        var size = host.Bounds.Size;

        var menuButton = (Button)Part(main, "PART_MenuButton");
        var viewMode = Item(((MenuFlyout)menuButton.Flyout!).Items, "View Mode");
        Assert.Equal(5, viewMode.Items.Count); // all five modes on a windowed platform (TW-5.16)
        Invoke(Item(viewMode.Items, "Float"));

        var a = Panel(main, "a");
        Assert.Equal(ToolWindowMode.Float, a.Mode);
        // No saved bounds existed: the command adopted the content's screen bounds supplied
        // by the UI at click time (TW-5.6, ADR-0002).
        Assert.Equal(new FloatingBounds(origin.X, origin.Y, size.Width, size.Height), a.FloatingBounds);
        Assert.Equal(new PixelPoint(origin.X, origin.Y), FloatingWindowOf(main, "a")!.Position);
    }

    [AvaloniaFact]
    public void TW_5_9_moving_and_resizing_the_window_commit_bounds_without_oscillation()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(40, 50, 320, 240));
        var main = Show(state, registry, lifecycle: lifecycle);
        var floating = FloatingWindowOf(main, "a")!;
        var changes = new StateChangeCounter(main);

        floating.Position = new PixelPoint(200, 150);

        // The commit is deferred onto the dispatcher: a synchronous command from a window
        // event would re-enter Sync inside a running layout pass — Show() of a new floating
        // window runs its initial pass inside the projection, and the OS adjusts bounds
        // during it (the desktop crash of 2026-07).
        Assert.Equal(0, changes.Count);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, changes.Count); // one SetFloatingBounds, no feedback loop
        Assert.Equal(new FloatingBounds(200, 150, 320, 240), Panel(main, "a").FloatingBounds);

        floating.Width = 400;
        floating.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, changes.Count);
        Assert.Equal(new FloatingBounds(200, 150, 400, 240), Panel(main, "a").FloatingBounds);
    }

    [AvaloniaFact]
    public void TW_9_3_materializing_a_record_without_saved_bounds_is_no_command()
    {
        // A restored floating record may carry no bounds: the window shows at the UI default
        // (relative to the main window) without writing it back — materialization never
        // mutates the state (TW-9.3, ADR-0004); the first user move records bounds (TW-5.9).
        var (registry, lifecycle, state, _, _) = Setup(ToolWindowMode.Float, bounds: null);
        var before = state;
        var main = Show(state, registry, lifecycle: lifecycle);

        var floating = FloatingWindowOf(main, "a");
        Assert.NotNull(floating);
        Assert.Equal(new PixelPoint(100, 100), floating.Position); // the main window inset default
        Assert.Same(before, St(main));
        Assert.Null(Panel(main, "a").FloatingBounds);
    }

    // ---- TW-9.13: the layer-change reattachment ----

    [AvaloniaFact]
    public void TW_9_13_layer_changes_reattach_the_same_host_and_view_both_ways()
    {
        var (registry, lifecycle, state, body, factory) = Setup(ToolWindowMode.DockPinned);
        var main = Show(state, registry, lifecycle: lifecycle);
        var host = Workspace(main).GetHost("a");
        Assert.Same(main, TopLevel.GetTopLevel(host));

        Workspace(main).State = St(main).SetMode("a", ToolWindowMode.Float, new FloatingBounds(20, 30, 300, 200));
        Dispatcher.UIThread.RunJobs();

        Assert.Same(host, Workspace(main).GetHost("a"));
        Assert.Same(FloatingWindowOf(main, "a"), TopLevel.GetTopLevel(host));
        Assert.Contains(body, host.GetVisualDescendants()); // the built view moved with the host

        Workspace(main).State = St(main).SetMode("a", ToolWindowMode.DockPinned);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(host, Workspace(main).GetHost("a"));
        Assert.Same(main, TopLevel.GetTopLevel(host));
        Assert.Contains(body, host.GetVisualDescendants());
        Assert.Equal(1, factory.Created); // the view was built once (TW-9.13)
    }

    [AvaloniaFact]
    public void TW_9_13_move_from_a_live_floating_window_into_the_main_window()
    {
        // The owner's desktop crash (2026-07): a tab moved out of a floating window that
        // stays alive left a stale entry in the source window's layout queue, and that
        // LayoutManager crashed on a control meanwhile rooted in the main window. The
        // draining detach kills the stale entry at the move itself, in every direction.
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "p", "Panel", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            ContentFactory = new CountingBodyFactory(_ => new TextBox()),
            TabFactory = new CountingTabFactory("p:"),
        });
        state = state.OpenPanelTab("p:t1", registry);
        state = state with
        {
            ToolWindows =
            [
                .. state.ToolWindows.Select(w => w with
                {
                    Mode = ToolWindowMode.Float,
                    IsOpen = true,
                    FloatingBounds = new FloatingBounds(40, 50, 400, 300),
                }),
            ],
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var floating = FloatingWindowOf(main, "p")!;
        var host = Workspace(main).TabHosts.GetHost("p:t1");

        Invoke(Item(((MenuFlyout)TabHeader(floating, "p:t1").ContextFlyout!).Items, "Move to Document Area"));
        Dispatcher.UIThread.RunJobs(); // detonation window of a stale layout-queue entry

        Assert.True(floating.IsVisible); // the source window survives the move
        Assert.Equal(["p:t1"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
        Assert.Same(host, Workspace(main).TabHosts.GetHost("p:t1"));
        Assert.Same(main, TopLevel.GetTopLevel(host)); // the same host reattached (DA-9.6)
    }

    // ---- DA-5.7, DA-7.2, DA-7.3: document windows ----

    [AvaloniaFact]
    public void DA_7_2_moving_one_tab_out_keeps_the_source_document_window_alive()
    {
        // The live-source direction of the same stale-queue race, between two dock hosts.
        var (registry, lifecycle, _, _) = DockSetup();
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(50, 60, 400, 300), Group("d9", "d8", "d9"), "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var osWindow = DocumentWindowOf(main, "d9")!;
        var host = Workspace(main).TabHosts.GetHost("d9");

        Invoke(Item(((MenuFlyout)TabHeader(osWindow, "d9").ContextFlyout!).Items, "Move to Document Area"));
        Dispatcher.UIThread.RunJobs(); // detonation window of a stale layout-queue entry

        Assert.True(osWindow.IsVisible); // the source window keeps d8 (DA-7.2)
        Assert.Equal(["d8"], Assert.IsType<TabGroupNode>(St(main).DockArea.Windows[0].Root).Tabs);
        Assert.Equal("d8", St(main).DockArea.Windows[0].CurrentTabId);
        Assert.Equal(["d1", "d9"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
        Assert.Same(main, TopLevel.GetTopLevel(host));
    }

    [AvaloniaFact]
    public void DA_5_7_move_to_new_window_creates_a_document_window()
    {
        var (registry, lifecycle, boxes, factory) = DockSetup();
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState { Root = Group("d2", "d1", "d2"), CurrentTabId = "d2" },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var host = Workspace(main).TabHosts.GetHost("d2");
        var d2Box = boxes["d2"];

        Invoke(Item(((MenuFlyout)TabHeader(main, "d2").ContextFlyout!).Items, "Move to New Window"));

        var area = St(main).DockArea;
        var docWindow = Assert.Single(area.Windows);
        Assert.Equal(["d2"], Assert.IsType<TabGroupNode>(docWindow.Root).Tabs);
        Assert.Equal("d2", docWindow.CurrentTabId);
        Assert.Equal(DockHost.DocumentWindow(0), area.ActiveDockHost); // activity followed (DA-5.4)
        // Default bounds: the main window inset, no cascade (DA-E-C4; owner decision).
        Assert.Equal(new FloatingBounds(100, 100, 600, 400), docWindow.Bounds);

        var osWindow = DocumentWindowOf(main, "d2");
        Assert.NotNull(osWindow);
        Assert.Equal(new PixelPoint(100, 100), osWindow.Position);
        Assert.Same(host, Workspace(main).TabHosts.GetHost("d2")); // the same host moved (DA-9.6)
        Assert.Same(d2Box, boxes["d2"]); // a move never recreates content (DA-5.4)
        Assert.True(d2Box.IsKeyboardFocusWithin || d2Box.IsFocused); // focus followed (DA-6.4)
        Assert.Empty(LayoutInvariants.Validate(St(main), registry));
    }

    [AvaloniaFact]
    public void DA_7_3_system_close_of_a_document_window_closes_every_tab()
    {
        var (registry, lifecycle, _, factory) = DockSetup();
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(50, 60, 400, 300), Group("d9", "d8", "d9"), "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var osWindow = DocumentWindowOf(main, "d9")!;
        var changes = new StateChangeCounter(main);

        osWindow.Close(); // the user's system close button
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, changes.Count); // one CloseTab per tab (DA-7.3, ADR-0004)
        Assert.Empty(St(main).DockArea.Windows); // the emptied window is gone (INV-D6)
        Assert.Equal(["d1"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
        Assert.Equal(1, factory.Released); // d9 was materialized (active) and released; d8 never materialized
        Assert.Null(DocumentWindowOf(main, "d9"));
    }

    [AvaloniaFact]
    public void DA_7_2_moving_the_last_tab_back_removes_the_window_and_keeps_the_view()
    {
        var (registry, lifecycle, boxes, factory) = DockSetup();
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(50, 60, 400, 300), Group("d9", "d9"), "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var osWindow = DocumentWindowOf(main, "d9")!;
        var host = Workspace(main).TabHosts.GetHost("d9");
        var created = factory.Created;

        Invoke(Item(((MenuFlyout)TabHeader(osWindow, "d9").ContextFlyout!).Items, "Move to Document Area"));

        Assert.Empty(St(main).DockArea.Windows); // the emptied window disappeared (DA-7.2, INV-D6)
        Assert.Equal(["d1", "d9"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
        Assert.False(osWindow.IsVisible);
        Assert.Same(host, Workspace(main).TabHosts.GetHost("d9")); // the same host reattached (DA-9.6)
        Assert.Same(main, TopLevel.GetTopLevel(host));
        Assert.Equal(created, factory.Created); // the built view survived the move
        Assert.True(boxes["d9"].IsKeyboardFocusWithin || boxes["d9"].IsFocused); // focus followed (DA-6.4)
    }

    [AvaloniaFact]
    public void DA_6_4_focus_in_a_document_window_activates_its_host()
    {
        var (registry, lifecycle, boxes, _) = DockSetup();
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(50, 60, 400, 300), Group("d9", "d9"), "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);

        Assert.True(boxes["d9"].Focus());
        Dispatcher.UIThread.RunJobs();

        // The focus gain reduced to ActivateTab (DA-6.4): the document window became the
        // active dock host (DA-5.3).
        Assert.Equal(DockHost.DocumentWindow(0), St(main).DockArea.ActiveDockHost);
        Assert.Null(St(main).ActiveToolWindowId);
    }

    [AvaloniaFact]
    public void DA_5_6_splitter_of_a_document_window_commits_to_its_host()
    {
        var (registry, lifecycle, _, _) = DockSetup();
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(
                        new FloatingBounds(50, 60, 600, 400),
                        new SplitNode
                        {
                            Orientation = SplitOrientation.Row,
                            Children = [new(Group("d8", "d8"), 0.5), new(Group("d9", "d9"), 0.5)],
                        },
                        "d9"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var osWindow = DocumentWindowOf(main, "d9")!;
        var w8 = Workspace(main).TabHosts.GetHost("d8").Bounds.Width;
        var w9 = Workspace(main).TabHosts.GetHost("d9").Bounds.Width;

        var start = Center(Part(osWindow, "PART_DockSplitter"), osWindow);
        var end = new Point(start.X + 80, start.Y);
        osWindow.MouseDown(start, MouseButton.Left);
        osWindow.MouseMove(end);
        osWindow.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // The commit addressed the document window's tree (DA-5.6, DA-1.3): the main tree is
        // untouched, the window's shares follow the drag.
        Assert.IsType<TabGroupNode>(St(main).DockArea.Root);
        var split = Assert.IsType<SplitNode>(St(main).DockArea.Windows[0].Root);
        Assert.Equal((w8 + 80) / (w8 + w9), split.Children[0].Share, 0.02);
        Assert.Equal(1.0, split.Children[0].Share + split.Children[1].Share, precision: 9);
    }

    // ---- TW-6.1: auto-hide across windows ----

    [AvaloniaFact]
    public void TW_6_1_focus_moving_into_a_float_window_closes_the_unpinned_panel()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var unpinnedBox = new TextBox();
        var floatBox = new TextBox();
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "u", "Unpinned", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            ContentFactory = new CountingBodyFactory(_ => unpinnedBox),
        });
        state = lifecycle.Register(state, new ToolWindowDescriptor(
            "f", "Floaty", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary))
        {
            ContentFactory = new CountingBodyFactory(_ => floatBox),
        });
        state = state with
        {
            ToolWindows =
            [
                state.ToolWindows[0] with { Mode = ToolWindowMode.DockUnpinned, LastInternalMode = ToolWindowMode.DockUnpinned, IsOpen = true },
                state.ToolWindows[1] with { Mode = ToolWindowMode.Float, IsOpen = true, FloatingBounds = new FloatingBounds(40, 50, 300, 200) },
            ],
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        Assert.True(unpinnedBox.Focus());
        Dispatcher.UIThread.RunJobs();
        Assert.True(Panel(main, "u").IsOpen);

        // A focus move between the workspace's own windows is an ordinary focus loss
        // (TW-6.1, task 6.0): the unpinned focus loser closes.
        Assert.True(floatBox.Focus());
        Dispatcher.UIThread.RunJobs();

        Assert.False(Panel(main, "u").IsOpen);
        Assert.True(Panel(main, "f").IsOpen);
    }

    // ---- TW-7.1, TW-7.2: platform chrome — the frameless Float and the title suffix ----

    /// <summary>The frameless-Float platform gate is Windows-only (TW-7.1); headless runs force it through the seam.</summary>
    private static Window ShowFrameless(LayoutState state, ToolWindowRegistry registry, ContentLifecycle lifecycle)
    {
        var workspace = new BerthWorkspace
        {
            ForceFramelessFloat = true,
            State = state,
            Registry = registry,
            Lifecycle = lifecycle,
        };
        var window = new Window { Width = 800, Height = 600, Content = workspace };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void TW_7_1_frameless_float_keeps_window_mode_decorated()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(200, 200, 320, 240));
        var main = ShowFrameless(state, registry, lifecycle);

        var floating = FloatingWindowOf(main, "a")!;
        Assert.Equal(WindowDecorations.None, floating.WindowDecorations); // frameless Float (TW-7.1)

        Workspace(main).State = St(main).SetMode("a", ToolWindowMode.Window);
        Dispatcher.UIThread.RunJobs();

        // Window mode keeps full system chrome on every platform (TW-7.2).
        var windowed = FloatingWindowOf(main, "a")!;
        Assert.NotSame(floating, windowed);
        Assert.NotEqual(WindowDecorations.None, windowed.WindowDecorations);
    }

    [AvaloniaFact]
    public void TW_7_2_independent_window_titles_carry_the_application_suffix()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Window, new FloatingBounds(40, 50, 320, 240));
        state = state with
        {
            DockArea = new DockAreaState
            {
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(60, 70, 300, 200), Group("x", "x"), "x"),
                ],
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        Assert.Equal("Alpha", FloatingWindowOf(main, "a")!.Title); // no suffix configured

        Workspace(main).WindowTitleSuffix = "Ursa";
        Dispatcher.UIThread.RunJobs();

        // Independent top-levels live in the taskbar and Alt-Tab: the suffix names the
        // application there (TW-7.2, DA-7.3; = IDEA «Title - Project»).
        Assert.Equal("Alpha - Ursa", FloatingWindowOf(main, "a")!.Title);
        Assert.Equal("x - Ursa", DocumentWindowOf(main, "x")!.Title);

        // Float is owned and outside the taskbar: its title stays bare (TW-7.2).
        Workspace(main).State = St(main).SetMode("a", ToolWindowMode.Float);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Alpha", FloatingWindowOf(main, "a")!.Title);
    }

    [AvaloniaFact]
    public void TW_7_1_frameless_header_move_commits_one_set_floating_bounds()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(200, 200, 320, 240));
        var main = ShowFrameless(state, registry, lifecycle);
        var floating = FloatingWindowOf(main, "a")!;
        var headerLocal = Center(Part(floating, "PART_Header"), floating);
        var changes = new StateChangeCounter(main);

        floating.MouseDown(headerLocal, MouseButton.Left);
        floating.MouseMove(new Point(headerLocal.X + 60, headerLocal.Y + 40));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new PixelPoint(260, 240), floating.Position); // the window moves live
        Assert.Equal(0, changes.Count); // pure visualization until the release (TW-7.1)

        floating.MouseUp(headerLocal, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, changes.Count); // exactly one SetFloatingBounds (TW-5.9)
        Assert.Equal(new FloatingBounds(260, 240, 320, 240), Panel(main, "a").FloatingBounds);
    }

    [AvaloniaFact]
    public void TW_7_1_dragging_the_frameless_header_onto_a_stripe_docks_the_panel()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(200, 200, 320, 240));
        var main = ShowFrameless(state, registry, lifecycle);
        var floating = FloatingWindowOf(main, "a")!;
        var headerLocal = Center(Part(floating, "PART_Header"), floating);
        // The panel's own stripe icon sits on Left.Primary — a robust point inside a stripe
        // drop zone; the main window is at (0,0), so its local point is the gesture point.
        var stripeGesture = Center(Button(main, "a"), main);

        floating.MouseDown(headerLocal, MouseButton.Left);
        floating.MouseMove(new Point(
            stripeGesture.X - floating.Position.X, stripeGesture.Y - floating.Position.Y));
        Dispatcher.UIThread.RunJobs();

        // The move lit the stripe zone in the main window's overlay (TW-7.1, mirror of
        // TW-7.7): the same insertion marker as the slot gesture, before any command.
        Assert.True(Part(main, "PART_DropMarker").IsVisible);
        Assert.Equal(ToolWindowMode.Float, Panel(main, "a").Mode); // still pure visualization

        floating.MouseUp(headerLocal, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // The release over the stripe docked the panel — the reverse of TW-7.8: Move +
        // SetMode(LastInternalMode); the OS window is gone, the marker hidden.
        Assert.Equal(ToolWindowMode.DockPinned, Panel(main, "a").Mode);
        Assert.True(Panel(main, "a").IsOpen);
        Assert.Null(FloatingWindowOf(main, "a"));
        Assert.False(Part(main, "PART_DropMarker").IsVisible);
    }

    [AvaloniaFact]
    public void TW_7_1_ctrl_at_release_parks_the_frameless_float_without_docking()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(200, 200, 320, 240));
        var main = ShowFrameless(state, registry, lifecycle);
        var floating = FloatingWindowOf(main, "a")!;
        var headerLocal = Center(Part(floating, "PART_Header"), floating);
        var stripeGesture = Center(Button(main, "a"), main);
        var changes = new StateChangeCounter(main);

        floating.MouseDown(headerLocal, MouseButton.Left);
        floating.MouseMove(
            new Point(stripeGesture.X - floating.Position.X, stripeGesture.Y - floating.Position.Y),
            RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        // Ctrl parks the window: the stripe zones stay glued shut, no marker (TW-7.1).
        Assert.False(Part(main, "PART_DropMarker").IsVisible);

        floating.MouseUp(headerLocal, MouseButton.Left, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        // The panel moved, it did not dock: still Float, one SetFloatingBounds.
        Assert.Equal(ToolWindowMode.Float, Panel(main, "a").Mode);
        Assert.NotNull(FloatingWindowOf(main, "a"));
        Assert.Equal(1, changes.Count);
    }

    [AvaloniaFact]
    public void TW_7_1_escape_restores_the_frameless_float_without_a_command()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(200, 200, 320, 240));
        var main = ShowFrameless(state, registry, lifecycle);
        var floating = FloatingWindowOf(main, "a")!;
        var headerLocal = Center(Part(floating, "PART_Header"), floating);
        var before = St(main);

        floating.MouseDown(headerLocal, MouseButton.Left);
        floating.MouseMove(new Point(headerLocal.X + 80, headerLocal.Y));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new PixelPoint(280, 200), floating.Position);

        PressEscape(floating); // restores the starting position (TW-7.1)
        Assert.Equal(new PixelPoint(200, 200), floating.Position);

        floating.MouseUp(new Point(headerLocal.X + 80, headerLocal.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(before, St(main)); // no command, no trace
    }

    [AvaloniaFact]
    public void TW_7_1_escape_from_the_focused_main_window_cancels_the_header_move()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(200, 200, 320, 240));
        var main = ShowFrameless(state, registry, lifecycle);
        var floating = FloatingWindowOf(main, "a")!;
        var headerLocal = Center(Part(floating, "PART_Header"), floating);
        var before = St(main);

        floating.MouseDown(headerLocal, MouseButton.Left);
        floating.MouseMove(new Point(headerLocal.X + 80, headerLocal.Y));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new PixelPoint(280, 200), floating.Position);

        // The header press never takes keyboard focus (TW-6.6), so the key may arrive at
        // the main window instead of the moved one — the gesture must hear it there too.
        PressEscape(main);
        Assert.Equal(new PixelPoint(200, 200), floating.Position);

        floating.MouseUp(new Point(headerLocal.X + 80, headerLocal.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(before, St(main)); // no command, no trace
    }

    [AvaloniaFact]
    public void TW_5_17_frameless_marker_overlay_sits_at_the_window_origin()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(200, 200, 320, 240));
        var main = ShowFrameless(state, registry, lifecycle);
        var floating = FloatingWindowOf(main, "a")!;

        // Cross-window drags address the marker overlay in window-local coordinates
        // (WindowedDragVisual, task 6.2): the frame band must not inset it, or every
        // marker inside the frameless window would shift by the band.
        var markers = ((FloatingWindowLayer.FloatingWindowBase)floating).Markers;
        Assert.Equal(new Point(0, 0), markers.TranslatePoint(default, floating));
    }

    [AvaloniaFact]
    public void TW_6_6_frameless_header_click_activates_without_moving()
    {
        var (registry, lifecycle, state, body, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(200, 200, 320, 240));
        var main = ShowFrameless(state, registry, lifecycle);
        var floating = FloatingWindowOf(main, "a")!;
        var before = St(main);

        Click(floating, Part(floating, "PART_Header"));

        // The deferred header activation (TW-6.6): focus moved into the content, which the
        // activity wiring reduced to the panel activation (DA-6.4); the window did not move.
        Assert.True(body.IsKeyboardFocusWithin || body.IsFocused);
        Assert.Equal("a", St(main).ActiveToolWindowId);
        Assert.Equal(before.ToolWindows[0].FloatingBounds, Panel(main, "a").FloatingBounds);
        Assert.Equal(new PixelPoint(200, 200), floating.Position);
    }

    // ---- TW-7.4: the screen-visibility validator ----

    [AvaloniaFact]
    public void TW_7_4_validator_replaces_offscreen_bounds_and_keeps_visible_ones()
    {
        var (registry, lifecycle, state, _, _) = Setup(ToolWindowMode.DockPinned);
        var main = Show(state, registry, lifecycle: lifecycle);
        var validator = FloatingBoundsValidation.CreateValidator(main);

        // Fully inside the headless screen (1920×1280): kept as saved.
        Assert.Null(validator(new FloatingBounds(100, 100, 400, 300)));

        // Far outside every screen: replaced relative to the main window (TW-7.4) — its
        // rectangle inset by 100 on each side (the 800×600 test window at 0,0).
        var replaced = validator(new FloatingBounds(50000, 50000, 300, 200));
        Assert.Equal(new FloatingBounds(100, 100, 600, 400), replaced);

        // Degenerate sizes cannot be judged by intersection: replaced too.
        Assert.NotNull(validator(new FloatingBounds(10, 10, 0, 0)));

        // The Apply integration (TW-7.4 [Core] + the UI validator): the fix is reported once.
        var offscreen = St(main) with
        {
            ToolWindows =
            [
                .. St(main).ToolWindows.Select(w => w with
                {
                    FloatingBounds = new FloatingBounds(50000, 50000, 300, 200),
                }),
            ],
        };
        var result = St(main).Apply(offscreen, ApplyScope.Full, registry, validator);
        var fix = Assert.Single(result.Fixes);
        Assert.Equal("TW-7.4", fix.Rule);
        Assert.Equal(new FloatingBounds(100, 100, 600, 400), result.State.ToolWindows[0].FloatingBounds);
    }
}
