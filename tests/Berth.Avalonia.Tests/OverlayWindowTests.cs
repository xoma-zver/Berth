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
/// The floating layer on a platform without real windows (task 6.1): Float tool windows — and
/// stored Window ones degraded by TW-7.6 — as pseudo-windows in the workspace overlay
/// (spec TW-7.7), document pseudo-windows (DA-7.5), window gestures reduced to core commands
/// (ADR-0004), capability-driven menus (TW-5.16), the focus and auto-hide wiring across
/// pseudo-windows (TW-6.1, TW-6.6, DA-6.4) and the workspace-bounds validator (TW-7.4). The
/// browser platform is unreachable headless — its TopLevel would not be a Window — so the
/// overlay layer is forced through the internal test seam.
/// </summary>
public class OverlayWindowTests
{
    private static Window ShowOverlay(
        LayoutState state,
        ToolWindowRegistry registry,
        ContentLifecycle? lifecycle = null,
        double width = 800,
        double height = 600)
    {
        var workspace = new BerthWorkspace
        {
            ForceOverlayFloating = true,
            State = state,
            Registry = registry,
            Lifecycle = lifecycle,
        };
        var window = new Window { Width = width, Height = height, Content = workspace };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static ToolWindowState Panel(Window window, string id) =>
        St(window).ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static Canvas PseudoCanvas(Window window) => (Canvas)Part(window, "PART_PseudoWindowLayer");

    private static IReadOnlyList<PseudoWindow> PseudoWindows(Window window) =>
        [.. PseudoCanvas(window).Children.OfType<PseudoWindow>()];

    private static PseudoWindow PanelPseudo(Window window, string id) =>
        PseudoWindows(window).Single(p => string.Equals(p.PanelId, id, StringComparison.Ordinal));

    private static PseudoWindow DocumentPseudo(Window window, string tabId) =>
        PseudoWindows(window).Single(p => p.PanelId is null && p.Tabs.Contains(tabId));

    private static Rect RectOf(PseudoWindow pseudo) =>
        new(Canvas.GetLeft(pseudo), Canvas.GetTop(pseudo), pseudo.Bounds.Width, pseudo.Bounds.Height);

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

        public object CreateContent(string toolWindowId)
        {
            Created++;
            return create(toolWindowId);
        }

        public void ReleaseContent(string toolWindowId, object content)
        {
        }
    }

    /// <summary>One tool window «a» in the given mode, open, with a TextBox body.</summary>
    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle, LayoutState State,
        TextBox Body, CountingBodyFactory Factory) Setup(ToolWindowMode mode, FloatingBounds? bounds = null)
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var body = new TextBox();
        var factory = new CountingBodyFactory(_ => body);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "a", "Alpha", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
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

    private static void Drag(Window window, Point start, Point end, bool release = true)
    {
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        Dispatcher.UIThread.RunJobs();
        if (release)
        {
            window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---- TW-7.7, TW-7.6: materialization and capabilities ----

    [AvaloniaFact]
    public void TW_7_7_float_panel_is_a_pseudo_window_hosting_the_cached_decorator()
    {
        var (registry, lifecycle, state, body, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(40, 50, 320, 240));
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);

        Assert.True(Workspace(main).CanFloat);
        Assert.False(Workspace(main).CanUseWindowed); // TW-7.6: no independent top-levels here
        var pseudo = PanelPseudo(main, "a");
        Assert.Equal(new Rect(40, 50, 320, 240), RectOf(pseudo)); // workspace coordinates (TW-7.7)
        Assert.Same(main, TopLevel.GetTopLevel(pseudo)); // one TopLevel: an overlay, not a window
        // The pseudo-window hosts the same cached decorator with the materialized body (TW-9.13).
        Assert.Same(Workspace(main).GetHost("a"), Decorator(pseudo, "a"));
        Assert.Contains(body, pseudo.GetVisualDescendants());
    }

    [AvaloniaFact]
    public void E13_stored_window_mode_presents_as_a_pseudo_float_keeping_the_mode()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Window, new FloatingBounds(40, 50, 320, 240));
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);

        // The effective mode degraded Window → Float (TW-7.6): the record materialized as a
        // pseudo-window; the stored mode never changed (E13).
        Assert.NotNull(PanelPseudo(main, "a"));
        Assert.Equal(ToolWindowMode.Window, Panel(main, "a").Mode);

        // «Dock» returns to the last internal mode (TW-5.16, E27).
        var menuButton = (Button)Part(main, "PART_MenuButton");
        var viewMode = Item(((MenuFlyout)menuButton.Flyout!).Items, "View Mode");
        Invoke(Item(viewMode.Items, "Dock"));
        Assert.Equal(ToolWindowMode.DockPinned, Panel(main, "a").Mode);
        Assert.Empty(PseudoWindows(main));
    }

    [AvaloniaFact]
    public void TW_5_16_view_mode_hides_only_window_and_supplements_dock_for_a_window_record()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(40, 50, 320, 240));
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);

        var menuButton = (Button)Part(main, "PART_MenuButton");
        var viewMode = Item(((MenuFlyout)menuButton.Flyout!).Items, "View Mode");
        // Four modes in the browser: Float is a pseudo-window (TW-7.7), only Window is hidden.
        Assert.Equal(
            ["Dock Pinned", "Dock Unpinned", "Undock", "Float"],
            viewMode.Items.OfType<MenuItem>().Select(i => (string)i.Header!));

        // A stored-Window record gets «Dock» supplementing the hidden current mode (TW-5.16).
        Workspace(main).State = St(main).SetMode("a", ToolWindowMode.Window);
        Dispatcher.UIThread.RunJobs();
        viewMode = Item(((MenuFlyout)((Button)Part(main, "PART_MenuButton")).Flyout!).Items, "View Mode");
        Assert.Equal(
            ["Dock Pinned", "Dock Unpinned", "Undock", "Float", "Dock"],
            viewMode.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
    }

    [AvaloniaFact]
    public void TW_9_13_layer_changes_reattach_the_same_host_and_view_both_ways()
    {
        var (registry, lifecycle, state, body, factory) = Setup(ToolWindowMode.DockPinned);
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        var host = Workspace(main).GetHost("a");

        Workspace(main).State = St(main).SetMode("a", ToolWindowMode.Float, new FloatingBounds(20, 30, 300, 200));
        Dispatcher.UIThread.RunJobs();

        Assert.Same(host, Workspace(main).GetHost("a"));
        Assert.Contains(host, PanelPseudo(main, "a").GetVisualDescendants());
        Assert.Contains(body, host.GetVisualDescendants()); // the built view moved with the host

        Workspace(main).State = St(main).SetMode("a", ToolWindowMode.DockPinned);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(PseudoWindows(main));
        Assert.Same(host, Workspace(main).GetHost("a"));
        Assert.Contains(body, host.GetVisualDescendants());
        Assert.Equal(1, factory.Created); // the view was built once (TW-9.13)
    }

    // ---- TW-7.7: move, resize, cancel ----

    [AvaloniaFact]
    public void TW_7_7_dragging_the_header_moves_the_pseudo_window_and_commits_once()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(40, 50, 320, 240));
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        var before = St(main);
        var changes = new StateChangeCounter(main);
        var header = Part(PanelPseudo(main, "a"), "PART_Header");
        var start = Center(header, main);

        main.MouseDown(start, MouseButton.Left);
        main.MouseMove(new Point(start.X + 120, start.Y + 60));
        Dispatcher.UIThread.RunJobs();

        // Pure visualization until the release: the pseudo-window follows, the state does not.
        Assert.Equal(new Rect(160, 110, 320, 240), RectOf(PanelPseudo(main, "a")));
        Assert.Same(before, St(main));

        main.MouseUp(new Point(start.X + 120, start.Y + 60), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, changes.Count); // exactly one SetFloatingBounds (TW-5.9, ADR-0004)
        Assert.Equal(new FloatingBounds(160, 110, 320, 240), Panel(main, "a").FloatingBounds);
    }

    [AvaloniaFact]
    public void TW_7_7_resizing_by_the_edge_commits_one_bounds_command()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(40, 50, 320, 240));
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        var changes = new StateChangeCounter(main);
        var rect = RectOf(PanelPseudo(main, "a"));
        var start = new Point(rect.Right - 2, rect.Center.Y); // the right resize band

        Drag(main, start, new Point(start.X + 80, start.Y));

        Assert.Equal(1, changes.Count);
        Assert.Equal(new FloatingBounds(40, 50, 400, 240), Panel(main, "a").FloatingBounds);
    }

    [AvaloniaFact]
    public void TW_7_7_escape_cancels_the_move_without_a_command()
    {
        var (registry, lifecycle, state, _, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(40, 50, 320, 240));
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        var before = St(main);
        var header = Part(PanelPseudo(main, "a"), "PART_Header");
        var start = Center(header, main);

        main.MouseDown(start, MouseButton.Left);
        main.MouseMove(new Point(start.X + 100, start.Y));
        Dispatcher.UIThread.RunJobs();
        PressEscape(main); // restores the starting rectangle (TW-7.7)
        main.MouseUp(new Point(start.X + 100, start.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(before, St(main)); // no command, no trace
        Assert.Equal(new Rect(40, 50, 320, 240), RectOf(PanelPseudo(main, "a")));
    }

    [AvaloniaFact]
    public void TW_6_6_a_header_click_without_movement_activates_instead_of_moving()
    {
        var (registry, lifecycle, state, body, _) = Setup(
            ToolWindowMode.Float, new FloatingBounds(40, 50, 320, 240));
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        var before = St(main);
        var header = Part(PanelPseudo(main, "a"), "PART_Header");

        Click(main, header);

        // The deferred header activation (TW-6.6): focus moved into the content, which the
        // activity wiring reduced to the panel activation (DA-6.4).
        Assert.True(body.IsKeyboardFocusWithin || body.IsFocused);
        Assert.Equal("a", St(main).ActiveToolWindowId);
        Assert.Equal(before.ToolWindows[0].FloatingBounds, Panel(main, "a").FloatingBounds); // no move
    }

    // ---- TW-6.6: z-order ----

    [AvaloniaFact]
    public void TW_6_6_pressing_and_focusing_raises_the_pseudo_window()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var state = LayoutState.Empty;
        foreach (var id in new[] { "a", "b" })
        {
            state = lifecycle.Register(state, new ToolWindowDescriptor(
                id, id, new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
            {
                ContentFactory = new CountingBodyFactory(i => boxes[i] = new TextBox()),
            });
        }

        state = state with
        {
            ToolWindows =
            [
                // Non-overlapping rectangles: a click must land on the intended pseudo-window.
                .. state.ToolWindows.Select((w, i) => w with
                {
                    Mode = ToolWindowMode.Float,
                    IsOpen = true,
                    Order = i,
                    FloatingBounds = new FloatingBounds(40 + (i * 360), 50 + (i * 250), 300, 200),
                }),
            ],
        };
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        var canvas = PseudoCanvas(main);
        var a = PanelPseudo(main, "a");
        var b = PanelPseudo(main, "b");

        // A press inside raises (TW-6.6); z-order is the canvas child order (DA-7.1: not persisted).
        Click(main, Part(a, "PART_Header"));
        Assert.Same(a, canvas.Children[^1]);

        // A focus gain raises too — the overlay equivalent of window activation.
        Assert.True(boxes["b"].Focus());
        Dispatcher.UIThread.RunJobs();
        Assert.Same(b, canvas.Children[^1]);
    }

    // ---- DA-7.5: document pseudo-windows ----

    [AvaloniaFact]
    public void DA_7_5_document_window_is_a_pseudo_window_projecting_its_tree()
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
                    new DocumentWindowState(new FloatingBounds(50, 60, 400, 300), Group("d9", "d8", "d9"), "d9"),
                ],
            },
        };
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);

        var pseudo = DocumentPseudo(main, "d9");
        Assert.Equal(new Rect(50, 60, 400, 300), RectOf(pseudo));
        // The title bar shows the current tab's display string (DA-7.5).
        Assert.Contains(pseudo.GetVisualDescendants().OfType<TextBlock>(), t =>
            string.Equals(t.Text, "d9", StringComparison.Ordinal));
        // The tree projects over the shared host cache: the active tab materialized lazily.
        Assert.Same(Workspace(main).TabHosts.GetHost("d9"), TabHost(pseudo, "d9"));
        Assert.Contains(boxes["d9"], pseudo.GetVisualDescendants());
    }

    [AvaloniaFact]
    public void DA_7_3_the_close_button_closes_every_tab()
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
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        var pseudo = DocumentPseudo(main, "d9");
        var changes = new StateChangeCounter(main);

        Click(main, Part(pseudo, "PART_PseudoWindowClose"));

        Assert.Equal(2, changes.Count); // one CloseTab per tab (DA-7.3, ADR-0004)
        Assert.Empty(St(main).DockArea.Windows); // the emptied window is gone (INV-D6)
        Assert.Empty(PseudoWindows(main));
        Assert.Equal(1, factory.Released); // only the materialized d9 was ever created
    }

    [AvaloniaFact]
    public void DA_9_6_moving_a_tab_between_the_main_window_and_a_pseudo_window_reattaches_the_host()
    {
        var (registry, lifecycle, boxes, factory) = DockSetup();
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState { Root = Group("d2", "d1", "d2"), CurrentTabId = "d2" },
        };
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        var host = Workspace(main).TabHosts.GetHost("d2");

        Invoke(Item(((MenuFlyout)TabHeader(main, "d2").ContextFlyout!).Items, "Move to New Window"));

        var area = St(main).DockArea;
        var docWindow = Assert.Single(area.Windows);
        Assert.Equal(["d2"], Assert.IsType<TabGroupNode>(docWindow.Root).Tabs);
        // Default bounds: the workspace rectangle inset — the overlay's «screen» (TW-7.7).
        Assert.Equal(new FloatingBounds(100, 100, 600, 400), docWindow.Bounds);
        var pseudo = DocumentPseudo(main, "d2");
        Assert.Same(host, Workspace(main).TabHosts.GetHost("d2")); // the same host moved (DA-9.6)
        Assert.Contains(host, pseudo.GetVisualDescendants());
        Assert.True(boxes["d2"].IsKeyboardFocusWithin || boxes["d2"].IsFocused); // focus followed (DA-6.4)

        var created = factory.Created;
        Invoke(Item(((MenuFlyout)TabHeader(pseudo, "d2").ContextFlyout!).Items, "Move to Document Area"));

        Assert.Empty(St(main).DockArea.Windows); // the emptied window disappeared (INV-D6)
        Assert.Empty(PseudoWindows(main));
        Assert.Same(host, Workspace(main).TabHosts.GetHost("d2"));
        Assert.Null(host.FindAncestorOfType<PseudoWindow>());
        Assert.Equal(created, factory.Created); // the built view survived both moves
        Assert.Empty(LayoutInvariants.Validate(St(main), registry));
    }

    [AvaloniaFact]
    public void DA_E21_escape_targets_the_current_tab_of_the_active_pseudo_host()
    {
        var (registry, lifecycle, boxes, _) = DockSetup();
        var panelBody = new TextBox();
        var state = lifecycle.Register(
            LayoutState.Empty with
            {
                DockArea = new DockAreaState
                {
                    Root = Group("d1", "d1"),
                    CurrentTabId = "d1",
                    Windows =
                    [
                        new DocumentWindowState(new FloatingBounds(50, 60, 400, 300), Group("d9", "d9"), "d9"),
                    ],
                    ActiveDockHost = DockHost.DocumentWindow(0),
                },
            },
            new ToolWindowDescriptor("p", "Panel", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
            {
                ContentFactory = new CountingBodyFactory(_ => panelBody),
            });
        state = state.Open("p");
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        Assert.True(panelBody.Focus());
        Dispatcher.UIThread.RunJobs();

        PressEscape(main);

        // No degradation to the main window (DA-6.4): the active host's pseudo-window is
        // materialized, so Esc lands in its current tab (TW-6.3, DA-E21).
        Assert.True(boxes["d9"].IsKeyboardFocusWithin || boxes["d9"].IsFocused);
    }

    // ---- TW-6.1: auto-hide across the pseudo-window boundary ----

    [AvaloniaFact]
    public void TW_6_1_focus_moving_into_a_pseudo_window_closes_the_unpinned_panel()
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
                state.ToolWindows[0] with
                {
                    Mode = ToolWindowMode.DockUnpinned,
                    LastInternalMode = ToolWindowMode.DockUnpinned,
                    IsOpen = true,
                },
                state.ToolWindows[1] with
                {
                    Mode = ToolWindowMode.Float,
                    IsOpen = true,
                    FloatingBounds = new FloatingBounds(400, 100, 300, 200),
                },
            ],
        };
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        Assert.True(unpinnedBox.Focus());
        Dispatcher.UIThread.RunJobs();
        Assert.True(Panel(main, "u").IsOpen);

        Assert.True(floatBox.Focus());
        Dispatcher.UIThread.RunJobs();

        // An ordinary focus loss (TW-6.1): the pseudo-window shares the TopLevel, and the
        // unpinned focus loser closes.
        Assert.False(Panel(main, "u").IsOpen);
        Assert.True(Panel(main, "f").IsOpen);
    }

    // ---- TW-5.17, DA-9.7: drag sources and targets in pseudo-windows (task 6.2) ----

    [AvaloniaFact]
    public void DA_9_7_tab_drags_between_a_document_pseudo_window_and_the_main_window()
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
                    new DocumentWindowState(new FloatingBounds(50, 60, 400, 300), Group("d9", "d8", "d9"), "d9"),
                ],
            },
        };
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        var pseudo = DocumentPseudo(main, "d9");
        var host = Workspace(main).TabHosts.GetHost("d8");

        // Out of the pseudo-window: d8's header drops into the center zone of the main
        // group, at a point clear of the pseudo-window itself (the top-window priority
        // would otherwise send the drop elsewhere) and of the edge wedges.
        var start = Center(TabHeader(pseudo, "d8"), main);
        var mainGroup = (Control)TabHost(main, "d1").GetVisualParent()!.GetVisualParent()!;
        var groupRect = BoundsIn(mainGroup, main);
        var target = new Point(
            groupRect.Right - (groupRect.Width * 0.3), groupRect.Bottom - (groupRect.Height * 0.3));
        main.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        main.MouseMove(target);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Part(main, "PART_DragGhost").IsVisible); // the overlay ghost lives in the DragLayer
        main.MouseUp(target, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // The cross-host move landed in the main group with activation (DA-5.4, DA-9.7); the
        // pseudo-window kept d9, the same cached host reattached (DA-9.6).
        Assert.Equal(["d1", "d8"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
        Assert.Equal("d8", St(main).DockArea.CurrentTabId);
        Assert.True(St(main).DockArea.ActiveDockHost.IsMainWindow);
        Assert.Equal(["d9"], Assert.IsType<TabGroupNode>(St(main).DockArea.Windows[0].Root).Tabs);
        Assert.Same(host, Workspace(main).TabHosts.GetHost("d8"));

        // Back in: d8's header drops into the pseudo-window group's strip end.
        var back = BoundsIn(TabHeader(DocumentPseudo(main, "d9"), "d9"), main);
        var backTarget = new Point(back.Right + 10, back.Center.Y);
        main.MouseDown(Center(TabHeader(main, "d8"), main), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        main.MouseMove(backTarget);
        Dispatcher.UIThread.RunJobs();
        main.MouseUp(backTarget, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var group = Assert.IsType<TabGroupNode>(St(main).DockArea.Windows[0].Root);
        Assert.Equal(["d9", "d8"], group.Tabs);
        Assert.Equal("d8", St(main).DockArea.Windows[0].CurrentTabId);
        Assert.Equal(DockHost.DocumentWindow(0), St(main).DockArea.ActiveDockHost); // ActivateTab followed
        Assert.Same(host, Workspace(main).TabHosts.GetHost("d8")); // the same host both ways
    }

    [AvaloniaFact]
    public void TW_5_17_zone_occluded_by_a_pseudo_window_does_not_fire()
    {
        // A float pseudo-window covers the middle of the main dock group: a tab dropped
        // there must not hit the group's center zone underneath (the top-window priority of
        // TW-5.17) — it lands in no zone at all, and the outside take-out of DA-9.7 moves it
        // into a new document pseudo-window at the point.
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new CountingTabFactory("d"));
        registry.Register(new ToolWindowDescriptor(
            "f", "Floaty", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary)));
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState { Root = Group("d1", "d1", "d2"), CurrentTabId = "d1" },
            ToolWindows =
            [
                Win("f", ToolWindowSide.Right, ToolWindowGroup.Primary) with
                {
                    Mode = ToolWindowMode.Float,
                    IsOpen = true,
                    FloatingBounds = new FloatingBounds(300, 200, 250, 200),
                },
            ],
        };
        var main = ShowOverlay(state, registry, lifecycle: new ContentLifecycle(registry));
        var pseudoRect = RectOf(PanelPseudo(main, "f"));
        var covered = pseudoRect.Center; // over the dock group, but the pseudo-window is on top

        main.MouseDown(Center(TabHeader(main, "d2"), main), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        main.MouseMove(covered);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Part(main, "PART_DropMarker").IsVisible); // the occluded center offers nothing
        main.MouseUp(covered, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var docWindow = Assert.Single(St(main).DockArea.Windows);
        Assert.Equal(["d2"], Assert.IsType<TabGroupNode>(docWindow.Root).Tabs);
        Assert.Equal(covered.X, docWindow.Bounds.X); // the new pseudo-window sits at the point
        Assert.Equal(covered.Y, docWindow.Bounds.Y);
        Assert.Equal(["d1"], Assert.IsType<TabGroupNode>(St(main).DockArea.Root).Tabs);
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
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        Assert.Equal(2, PseudoWindows(main).Count);
        var before = St(main);

        main.Close();
        Dispatcher.UIThread.RunJobs();

        // Teardown is no command (TW-7.5, DA-7.6): the state — the same instance — still
        // holds the open floating windows for the next session.
        Assert.Same(before, Workspace(main).State);
        Assert.True(before.ToolWindows[0].IsOpen);
        Assert.Single(before.DockArea.Windows);
    }

    // ---- TW-7.4: the workspace-bounds validator ----

    [AvaloniaFact]
    public void TW_7_4_overlay_validator_replaces_bounds_outside_the_workspace()
    {
        var (registry, lifecycle, state, _, _) = Setup(ToolWindowMode.DockPinned);
        var main = ShowOverlay(state, registry, lifecycle: lifecycle);
        var validator = FloatingBoundsValidation.CreateOverlayValidator(Workspace(main));

        // Fully inside the 800×600 workspace: kept as saved.
        Assert.Null(validator(new FloatingBounds(100, 100, 400, 300)));

        // Screen coordinates carried over from the desktop lie outside the workspace: replaced
        // by the workspace rectangle inset on every side (TW-7.4, TW-7.7).
        var replaced = validator(new FloatingBounds(5000, 5000, 300, 200));
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
                    FloatingBounds = new FloatingBounds(5000, 5000, 300, 200),
                }),
            ],
        };
        var result = St(main).Apply(offscreen, ApplyScope.Full, registry, validator);
        var fix = Assert.Single(result.Fixes);
        Assert.Equal("TW-7.4", fix.Rule);
        Assert.Equal(new FloatingBounds(100, 100, 600, 400), result.State.ToolWindows[0].FloatingBounds);
    }
}
