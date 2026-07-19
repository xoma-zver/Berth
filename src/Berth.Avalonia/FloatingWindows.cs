using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Berth.Controls;

/// <summary>
/// Materialization of the floating layer on a platform with real windows (TW-7.1…TW-7.5,
/// DA-7.1…DA-7.3): open Float/Window tool windows become OS windows hosting the same cached
/// <see cref="ToolWindowDecorator"/>, and every document window of the state becomes an
/// independent OS window projecting its tab tree over the workspace-wide host cache. A pure
/// projection of the state: panels reconcile by id, document windows by tab-set overlap —
/// they have no identity of their own (DA-1.3). Window gestures reduce to core commands: the
/// system close button issues Close or one CloseTab per tab, moves and resizes commit bounds
/// commands with equal-value guards breaking the feedback loop. Closing the main window — or
/// detaching the workspace — tears every floating window down without commands: the state
/// keeps the windows open for the next session (TW-7.5, DA-7.6). Presentation is
/// platform-specific (TW-7.1): on Windows a Float window is frameless — the decorator header
/// is the live move handle offering stripe docking — while macOS and Linux keep system
/// decorations, the float/window difference being the owned/independent window type. Titles
/// of independent windows carry <see cref="BerthWorkspace.WindowTitleSuffix"/> (TW-7.2).
/// </summary>
internal sealed class FloatingWindowLayer : IFloatingLayer
{
    private readonly BerthWorkspace _workspace;
    private readonly Window _owner;
    private readonly bool _framelessFloat;
    private readonly Dictionary<string, PanelWindow> _panels = new(StringComparer.Ordinal);
    private readonly List<DocumentWindow> _documents = [];
    private bool _torndown;

    public FloatingWindowLayer(BerthWorkspace workspace, Window owner)
    {
        _workspace = workspace;
        _owner = owner;
        // The frameless-Float platform gate of TW-7.1: Windows only — macOS and Linux keep
        // system decorations of the owned window; the seam covers headless runs.
        _framelessFloat = OperatingSystem.IsWindows() || workspace.ForceFramelessFloat;
        // Closing the main window must tear down the independent windows too — Window-mode
        // panels and document windows, which no platform cascade reaches (TW-7.5, DA-7.6).
        // The owned-Float cascade is recognized by CloseReason instead: it raises Closing on
        // the owned windows before this handler runs (headless probe, 2026-07).
        _owner.Closing += OnOwnerClosing;
    }

    /// <inheritdoc/>
    public bool IsWindowed => true;

    /// <summary>The reconciliation pass, run from the workspace projection (TW-9.13, DA-9.6).</summary>
    public void Update(LayoutState state, ToolWindowRegistry registry)
    {
        if (_torndown)
        {
            return;
        }

        UpdatePanels(state, registry);
        UpdateDocuments(state, registry);
    }

    /// <summary>
    /// Closes every floating window without commands and detaches from the owner — the
    /// teardown of TW-7.5/DA-7.6 (main window closing, workspace detaching): the state keeps
    /// the windows open for the next session. Idempotent.
    /// </summary>
    public void Teardown()
    {
        if (_torndown)
        {
            return;
        }

        _torndown = true;
        _owner.Closing -= OnOwnerClosing;
        foreach (var panel in _panels.Values.ToArray())
        {
            CloseSuppressed(panel);
        }

        _panels.Clear();
        foreach (var document in _documents.ToArray())
        {
            CloseSuppressed(document);
        }

        _documents.Clear();
    }

    private void OnOwnerClosing(object? sender, WindowClosingEventArgs e) => Teardown();

    // ---- Float/Window tool windows (TW-7.1, TW-7.2) ----

    private void UpdatePanels(LayoutState state, ToolWindowRegistry registry)
    {
        List<string>? stale = null;
        foreach (var id in _panels.Keys)
        {
            if (OpenFloating(state, id) is null)
            {
                (stale ??= []).Add(id);
            }
        }

        if (stale is not null)
        {
            foreach (var id in stale)
            {
                var panel = _panels[id];
                _panels.Remove(id);
                CloseSuppressed(panel);
            }
        }

        foreach (var window in state.ToolWindows)
        {
            if (!window.IsOpen || window.Mode.GetLayer() != ToolWindowLayer.Floating)
            {
                continue;
            }

            var independent = window.Mode == ToolWindowMode.Window;
            if (_panels.TryGetValue(window.Id, out var panel) && panel.IsIndependent != independent)
            {
                // Float ↔ Window re-hosts with the same bounds (TW-5.6): the decorator and
                // its view survive in the cache; only the OS window is replaced.
                _panels.Remove(window.Id);
                CloseSuppressed(panel);
                panel = null;
            }

            if (panel is null)
            {
                panel = new PanelWindow(_workspace, window.Id, independent, !independent && _framelessFloat)
                {
                    // Materialization must not steal focus: activation is a command concern
                    // (TW-6.6), not a projection side effect (TW-9.13).
                    ShowActivated = false,
                    ShowInTaskbar = independent, // Float stays out of the OS taskbar (TW-7.1)
                };
                _panels[window.Id] = panel;
                WireCommon(panel);
                panel.Closing += (_, e) => OnPanelClosing(panel, e);
                ApplyBounds(panel, window.FloatingBounds ?? _workspace.DefaultFloatingBounds());
                if (independent)
                {
                    panel.Show(); // independent top-level (TW-7.2)
                }
                else
                {
                    panel.Show(_owner); // owned: above the main window, minimizes with it (TW-7.1)
                }

                _workspace.AttachFloatingTopLevel(panel);
            }
            else if (window.FloatingBounds is { } bounds && bounds != panel.AppliedBounds)
            {
                ApplyBounds(panel, bounds);
            }

            // The suffix goes to independent windows only (TW-7.2): Float is owned and
            // outside the taskbar, its title stays bare.
            var title = registry.TryGet(window.Id, out var descriptor) ? descriptor.Title : window.Id;
            panel.Title = panel.IsIndependent ? WithTitleSuffix(title) : title;
            var host = _workspace.GetHost(window.Id);
            if (!ReferenceEquals(panel.HostSlot.Child, host))
            {
                if (panel.HostSlot.Child is { } previous)
                {
                    BerthWorkspace.DetachFromParent(previous);
                }

                BerthWorkspace.DetachFromParent(host);
                panel.HostSlot.Child = host;
            }
        }
    }

    private void OnPanelClosing(PanelWindow panel, WindowClosingEventArgs e)
    {
        if (!IsCloseGesture(panel, e))
        {
            return; // a projection, teardown or cascade close is no gesture
        }

        // The user's system close button is the Close command (TW-7.3): the platform close is
        // cancelled and the state change closes the window through the projection — the
        // command runs outside the Closing event to keep the close path non-reentrant.
        e.Cancel = true;
        var id = panel.ToolWindowId;
        Dispatcher.UIThread.Post(() => _workspace.Execute(s => s.Close(id)));
    }

    /// <summary>
    /// Whether the close is the user's gesture rather than plumbing: not a projection-driven
    /// close, not a teardown, and not the platform cascade of the closing owner or the
    /// shutting-down application (TW-7.5) — the owned-window cascade raises Closing on the
    /// owned windows before the owner's own Closing handlers run (headless probe, 2026-07),
    /// so the teardown flag alone cannot tell it apart, and cancelling a cascade close would
    /// cancel the owner's close too.
    /// </summary>
    private bool IsCloseGesture(FloatingWindowBase window, WindowClosingEventArgs e) =>
        !_torndown
        && !window.SuppressCommands
        && e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined;

    private void CommitPanelBounds(PanelWindow panel)
    {
        if (_torndown || panel.SuppressCommands || panel.Applying || !panel.IsVisible)
        {
            return;
        }

        var bounds = CurrentBounds(panel);
        if (bounds == panel.AppliedBounds)
        {
            // The window shows what the projection applied — no user gesture happened. This
            // also keeps a record without saved bounds command-free: the layout events of
            // the initial Show must not write the UI-invented default into the state.
            return;
        }

        var id = panel.ToolWindowId;
        var current = _workspace.State?.ToolWindows.FirstOrDefault(
            w => string.Equals(w.Id, id, StringComparison.Ordinal));
        if (current is null || current.FloatingBounds == bounds)
        {
            return; // the equal-value guard breaks the command → projection → event loop
        }

        panel.AppliedBounds = bounds; // what we commit is what the window shows — no re-apply
        _workspace.Execute(s => s.SetFloatingBounds(id, bounds));
    }

    private static ToolWindowState? OpenFloating(LayoutState state, string id) =>
        state.ToolWindows.FirstOrDefault(w =>
            string.Equals(w.Id, id, StringComparison.Ordinal)
            && w.IsOpen
            && w.Mode.GetLayer() == ToolWindowLayer.Floating);

    // ---- document windows (DA-7.1…DA-7.3) ----

    private void UpdateDocuments(LayoutState state, ToolWindowRegistry registry)
    {
        var windows = state.DockArea.Windows;
        var matched = new DocumentWindow?[windows.Length];
        var used = new bool[_documents.Count];
        for (var i = 0; i < windows.Length; i++)
        {
            var tabs = DockTrees.TabsOf(windows[i].Root);
            for (var j = 0; j < _documents.Count; j++)
            {
                // Windows have no identity (DA-1.3): live windows match state entries by tab
                // overlap, like groups in the tree reconciliation (DA-9.6).
                if (!used[j] && _documents[j].Tabs.Overlaps(tabs))
                {
                    used[j] = true;
                    matched[i] = _documents[j];
                    break;
                }
            }
        }

        for (var j = 0; j < _documents.Count; j++)
        {
            if (!used[j])
            {
                CloseSuppressed(_documents[j]);
            }
        }

        _documents.Clear();
        for (var i = 0; i < windows.Length; i++)
        {
            var view = matched[i];
            if (view is null)
            {
                view = new DocumentWindow(TabTreeContext.ForDocumentWindow(_workspace))
                {
                    ShowActivated = false,
                };
                WireCommon(view);
                view.Closing += (_, e) => OnDocumentClosing(view, e);
                ApplyBounds(view, windows[i].Bounds);
                view.Show(); // independent top-level (DA-7.3)
                _workspace.AttachFloatingTopLevel(view);
            }
            else if (windows[i].Bounds != view.AppliedBounds)
            {
                ApplyBounds(view, windows[i].Bounds);
            }

            view.Context.DocumentWindowIndex = i;
            view.Tabs.Clear();
            view.Tabs.UnionWith(DockTrees.TabsOf(windows[i].Root));
            // A document window is always independent: its title carries the suffix (DA-7.3).
            view.Title = WithTitleSuffix(TabHostCache.TitleOf(_workspace, windows[i].CurrentTabId));
            view.Context.ReconcileRoot(view.TreeSlot, windows[i].Root, state, registry);
            _documents.Add(view);
        }
    }

    private void OnDocumentClosing(DocumentWindow view, WindowClosingEventArgs e)
    {
        if (!IsCloseGesture(view, e))
        {
            return;
        }

        // The system close of a document window is CloseTab of every tab (DA-7.3) — a UI
        // composition over CloseTab, one command with one lifecycle report each; the emptied
        // window then disappears from the state (INV-D6) and the projection closes it.
        e.Cancel = true;
        Dispatcher.UIThread.Post(() => CloseAllTabs(view));
    }

    private void CloseAllTabs(DocumentWindow view)
    {
        if (_torndown || _workspace.State is not { } state)
        {
            return;
        }

        foreach (var window in state.DockArea.Windows)
        {
            if (!DockTrees.TabsOf(window.Root).Overlaps(view.Tabs))
            {
                continue;
            }

            foreach (var group in DockTrees.Groups(window.Root))
            {
                foreach (var tab in group.Tabs)
                {
                    _workspace.Execute(s => DockTrees.LayoutContainsTab(s, tab) ? s.CloseTab(tab) : s);
                }
            }

            return;
        }
    }

    private void CommitDocumentBounds(DocumentWindow view)
    {
        if (_torndown || view.SuppressCommands || view.Applying || !view.IsVisible)
        {
            return;
        }

        var bounds = CurrentBounds(view);
        if (bounds == view.AppliedBounds || _workspace.State is not { } state)
        {
            return; // the projection applied these bounds — no user gesture happened
        }

        // The window is addressed by any of its tabs (DA-5.8, DA-1.3).
        string? anchor = null;
        foreach (var window in state.DockArea.Windows)
        {
            var tabs = DockTrees.TabsOf(window.Root);
            if (tabs.Overlaps(view.Tabs))
            {
                if (window.Bounds == bounds)
                {
                    return; // the equal-value guard breaks the feedback loop
                }

                anchor = tabs.First();
                break;
            }
        }

        if (anchor is null)
        {
            return;
        }

        view.AppliedBounds = bounds;
        _workspace.Execute(s => DockTrees.LayoutContainsTab(s, anchor) ? s.SetDocumentWindowBounds(anchor, bounds) : s);
    }

    // ---- shared window plumbing ----

    /// <summary>The independent-window title suffix of TW-7.2/DA-7.3; a bare title without one.</summary>
    private string WithTitleSuffix(string title) =>
        _workspace.WindowTitleSuffix is { Length: > 0 } suffix ? $"{title} - {suffix}" : title;

    private void WireCommon(FloatingWindowBase window)
    {
        window.PositionChanged += (_, _) => ScheduleCommit(window);
        window.SizeChanged += (_, _) => ScheduleCommit(window);
        // The explicit commit of a completed move gesture (TW-7.1): position changes during
        // the gesture are pure visualization and schedule nothing — the release requests one.
        window.CommitRequested = () => ScheduleCommit(window);
    }

    /// <summary>
    /// Defers the bounds commit onto the dispatcher, coalescing event storms into one command
    /// (throttling is allowed, TW-5.9, DA-5.8). Never commits synchronously from the window
    /// event: on a native platform Show() runs the new window's initial layout pass inside
    /// the projection, and the OS adjusts bounds during it — a synchronous command there
    /// re-enters Sync inside a running layout pass and crashes the LayoutManager (owner's
    /// desktop run, 2026-07). The deferred commit re-reads the settled bounds and the live
    /// state, so the guards filter everything the projection itself caused. A live move
    /// gesture of a frameless Float window suppresses scheduling entirely (TW-7.1): the drag
    /// is pure visualization until its release, which requests the single commit itself.
    /// </summary>
    private void ScheduleCommit(FloatingWindowBase window)
    {
        if (_torndown || window.SuppressCommands || window.CommitScheduled || window.MoveGestureActive)
        {
            return;
        }

        window.CommitScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            window.CommitScheduled = false;
            CommitBounds(window);
        });
    }

    private void CommitBounds(FloatingWindowBase window)
    {
        switch (window)
        {
            case PanelWindow panel:
                CommitPanelBounds(panel);
                break;
            case DocumentWindow document:
                CommitDocumentBounds(document);
                break;
        }
    }

    private void CloseSuppressed(FloatingWindowBase window)
    {
        window.SuppressCommands = true;
        _workspace.DetachFloatingTopLevel(window);
        // The hosted content returns to its cache before the OS window goes away: the host
        // and its built view survive the window (TW-9.13, DA-9.6).
        window.ReleaseContent();
        window.Close();
    }

    private static void ApplyBounds(FloatingWindowBase window, FloatingBounds bounds)
    {
        window.Applying = true;
        try
        {
            window.Position = new PixelPoint(
                (int)Math.Round(bounds.X, MidpointRounding.AwayFromZero),
                (int)Math.Round(bounds.Y, MidpointRounding.AwayFromZero));
            window.Width = bounds.Width;
            window.Height = bounds.Height;
        }
        finally
        {
            window.Applying = false;
        }

        window.AppliedBounds = bounds;
    }

    private static FloatingBounds CurrentBounds(FloatingWindowBase window) =>
        new(window.Position.X, window.Position.Y, window.ClientSize.Width, window.ClientSize.Height);

    /// <summary>Shared flags of the layer's windows: reentrancy guards of the state ↔ window event loop.</summary>
    internal abstract class FloatingWindowBase : Window
    {
        /// <summary>True while the projection drives this window: its events are no gestures.</summary>
        public bool SuppressCommands { get; set; }

        /// <summary>True while bounds are being applied programmatically — the resulting events commit nothing.</summary>
        public bool Applying { get; set; }

        /// <summary>True while a deferred bounds commit is queued on the dispatcher — further events coalesce into it.</summary>
        public bool CommitScheduled { get; set; }

        /// <summary>
        /// True while a live move gesture drives this window (the frameless Float of TW-7.1):
        /// position changes are pure visualization and schedule no commits — the release
        /// requests the single one through <see cref="CommitRequested"/>.
        /// </summary>
        public bool MoveGestureActive { get; set; }

        /// <summary>Requests the layer's deferred bounds commit — wired by the layer, invoked by a completed move gesture.</summary>
        public Action? CommitRequested { get; set; }

        /// <summary>Last bounds applied or committed; an equal state value is not re-applied.</summary>
        public FloatingBounds? AppliedBounds { get; set; }

        /// <summary>Drop marker overlay of this window (TW-5.17, task 6.2): permanent chrome above the content.</summary>
        public MarkerOverlay Markers { get; } = new();

        /// <summary>Returns the hosted content to the workspace caches before the window closes.</summary>
        public abstract void ReleaseContent();

        /// <summary>Stacks the content slot under the drop marker overlay of the window.</summary>
        protected Panel WithMarkers(Control slot)
        {
            var root = new Panel();
            root.Children.Add(slot);
            root.Children.Add(Markers);
            return root;
        }
    }

    /// <summary>
    /// OS window of one Float/Window tool window (TW-7.1, TW-7.2), hosting its cached
    /// decorator. A frameless Float window (Windows, TW-7.1) carries its own thin frame: a
    /// border band along the edges starts the system resize, and the decorator's header —
    /// delegated by the decorator, like inside a pseudo-window (TW-7.7) — drives the live
    /// move gesture: the window follows the pointer with no state changes, the stripe dock
    /// zones light up under it (<see cref="PanelDockGuide"/>) unless <c>Ctrl</c> parks the
    /// window, a release over a zone docks by the target's own Move + SetMode composition
    /// (TW-7.8), a release elsewhere after actual movement requests exactly one
    /// SetFloatingBounds, Esc restores the starting position without a command, and a click
    /// that never moved runs the deferred header activation of TW-6.6.
    /// </summary>
    internal sealed class PanelWindow : FloatingWindowBase
    {
        /// <summary>Thickness of the resize band along the frameless window's edges.</summary>
        private const double ResizeBand = 6;

        private readonly BerthWorkspace _workspace;
        private bool _moveActive;
        private bool _moveMoved;
        private bool _moveCancelled;
        private Point _movePointerStart; // gesture (screen) coordinates
        private PixelPoint _movePositionStart;
        private PanelDockGuide? _dockGuide;
        private TopLevel? _moveKeyRoot;

        public PanelWindow(BerthWorkspace workspace, string toolWindowId, bool independent, bool frameless)
        {
            _workspace = workspace;
            ToolWindowId = toolWindowId;
            IsIndependent = independent;
            IsFrameless = frameless;
            if (frameless)
            {
                WindowDecorations = WindowDecorations.None;
                // The frame band sits inside the marker stack: the marker overlay must stay
                // at the window origin — WindowedDragVisual addresses it in window-local
                // coordinates, and a band-inset overlay would shift every marker by the band.
                Content = WithMarkers(new Border
                {
                    BorderBrush = BerthBrushes.Separator,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(ResizeBand - 1),
                    Background = Brushes.Transparent, // the band must stay hit-testable
                    Child = HostSlot,
                });
                AddHandler(PointerPressedEvent, OnFramelessPressed, RoutingStrategies.Tunnel);
            }
            else
            {
                Content = WithMarkers(HostSlot);
            }
        }

        public string ToolWindowId { get; }

        /// <summary>True for the Window mode (independent top-level), false for Float (owned).</summary>
        public bool IsIndependent { get; }

        /// <summary>True for the frameless Float presentation of TW-7.1 (Windows): the decorator header moves the window.</summary>
        public bool IsFrameless { get; }

        /// <summary>Slot the cached <see cref="ToolWindowDecorator"/> reattaches into (TW-9.13).</summary>
        public Decorator HostSlot { get; } = new();

        public override void ReleaseContent()
        {
            // Through the draining detach: the decorator re-docks into the main window next,
            // and this window's layout queue must not keep naming it (see
            // BerthWorkspace.DetachFromParent); the window is still alive here.
            if (HostSlot.Child is { } host)
            {
                BerthWorkspace.DetachFromParent(host);
            }
        }

        /// <summary>
        /// Starts the live move gesture — called by the hosted decorator delegating its bare
        /// header press (TW-7.1): inside a frameless window the header drags the window, not
        /// the slot-drag of TW-5.17.
        /// </summary>
        public void BeginHeaderMove(PointerPressedEventArgs e)
        {
            if (_moveActive)
            {
                return;
            }

            _moveActive = true;
            _moveMoved = false;
            _moveCancelled = false;
            MoveGestureActive = true; // position changes below are pure visualization (TW-7.1)
            _movePointerStart = GestureSpace.FromTopLevel(this, e.GetPosition(this));
            _movePositionStart = Position;
            e.Pointer.Capture(this);
            // Esc must reach the gesture wherever the keyboard focus sits: on Windows the
            // press activates this window and the key tunnels from its own root, but the
            // press itself never takes focus (deferred activation, TW-6.6) — a focus left
            // in the main window routes the key there instead, so the handler goes on both
            // (the pseudo-window precedent attaches to its focused TopLevel likewise).
            AddHandler(KeyDownEvent, OnMoveKeyDown, RoutingStrategies.Tunnel);
            _moveKeyRoot = TopLevel.GetTopLevel(_workspace);
            _moveKeyRoot?.AddHandler(KeyDownEvent, OnMoveKeyDown, RoutingStrategies.Tunnel);
        }

        /// <summary>The frameless edge band starts the system resize (TW-7.1); the resulting events commit through the ordinary deferred path (TW-5.9).</summary>
        private void OnFramelessPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _moveActive)
            {
                return;
            }

            var position = e.GetPosition(this);
            var x = position.X <= ResizeBand ? -1 : position.X >= Bounds.Width - ResizeBand ? 1 : 0;
            var y = position.Y <= ResizeBand ? -1 : position.Y >= Bounds.Height - ResizeBand ? 1 : 0;
            if (x == 0 && y == 0)
            {
                return;
            }

            e.Handled = true; // the band wins over content underneath
            BeginResizeDrag(EdgeOf(x, y), e);
        }

        /// <inheritdoc/>
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_moveActive || _moveCancelled || !ReferenceEquals(e.Pointer.Captured, this))
            {
                return;
            }

            // The pointer's screen position is stable while the window moves under it: the
            // gesture point derives from the event's window-local coordinates plus the
            // window's current position, so the delta from the press point stays exact.
            var current = GestureSpace.FromTopLevel(this, e.GetPosition(this));
            var delta = current - _movePointerStart;
            if (delta == default && !_moveMoved)
            {
                return;
            }

            _moveMoved = true;
            Position = new PixelPoint(
                _movePositionStart.X + (int)Math.Round(delta.X, MidpointRounding.AwayFromZero),
                _movePositionStart.Y + (int)Math.Round(delta.Y, MidpointRounding.AwayFromZero));

            // The move offers docking (TW-7.1, mirror of TW-7.7): the stripe zones of the
            // main window light up under the pointer, unless Ctrl parks the window at will.
            // The guide is built on the first real movement, so a mere header click pays
            // nothing.
            _dockGuide ??= _workspace.BeginPanelDockGuide(ToolWindowId);
            _dockGuide?.Update(current, suppressed: e.KeyModifiers.HasFlag(KeyModifiers.Control));
        }

        /// <inheritdoc/>
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_moveActive)
            {
                return;
            }

            var moved = _moveMoved && !_moveCancelled;
            var headerClick = !_moveMoved && !_moveCancelled;
            // A release over a stripe zone docks the panel instead of moving it (TW-7.1),
            // unless Ctrl parks it at the edge; resolved before EndMoveGesture drops the guide.
            var dockTarget = moved && !e.KeyModifiers.HasFlag(KeyModifiers.Control)
                ? _dockGuide?.Resolve(GestureSpace.FromTopLevel(this, e.GetPosition(this)))
                : null;
            EndMoveGesture();
            e.Pointer.Capture(null);
            if (dockTarget is { } target)
            {
                // The same Move + SetMode(LastInternalMode) composition as the reverse
                // icon/header drop (TW-7.8), through the workspace funnel. Deferred: the
                // docking re-projection closes this window, and that close must not run
                // inside its own pointer event (the non-reentrant close precedent of the
                // floating layer).
                var workspace = _workspace;
                Dispatcher.UIThread.Post(() => target.Commit(workspace));
            }
            else if (moved)
            {
                // One command per completed gesture (TW-7.1, TW-5.9); the deferred commit
                // re-reads the settled bounds and the live state.
                CommitRequested?.Invoke();
            }
            else if (headerClick && !IsKeyboardFocusWithin)
            {
                // The deferred header activation of TW-6.6: a header click that never became
                // a window move focuses the panel content, activating the panel by the
                // DA-6.4 wiring.
                (HostSlot.Child as ToolWindowDecorator)?.FocusContent();
            }
        }

        /// <inheritdoc/>
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (_moveActive)
            {
                if (!_moveCancelled)
                {
                    Position = _movePositionStart; // a lost capture cancels with no trace
                }

                EndMoveGesture();
            }
        }

        private void OnMoveKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _moveActive && !_moveCancelled)
            {
                // Esc restores the starting position without a command (TW-7.1); the capture
                // holds until the release, which then does nothing.
                _moveCancelled = true;
                _dockGuide?.Hide();
                Position = _movePositionStart;
                e.Handled = true;
            }
        }

        private void EndMoveGesture()
        {
            _moveActive = false;
            MoveGestureActive = false;
            _dockGuide?.Hide();
            _dockGuide = null;
            RemoveHandler(KeyDownEvent, OnMoveKeyDown);
            _moveKeyRoot?.RemoveHandler(KeyDownEvent, OnMoveKeyDown);
            _moveKeyRoot = null;
        }

        private static WindowEdge EdgeOf(int x, int y) => (x, y) switch
        {
            (-1, -1) => WindowEdge.NorthWest,
            (1, -1) => WindowEdge.NorthEast,
            (-1, 1) => WindowEdge.SouthWest,
            (1, 1) => WindowEdge.SouthEast,
            (-1, 0) => WindowEdge.West,
            (1, 0) => WindowEdge.East,
            (0, -1) => WindowEdge.North,
            _ => WindowEdge.South,
        };
    }

    /// <summary>OS window of one document window (DA-7.1), projecting its tab tree (DA-9.6).</summary>
    private sealed class DocumentWindow : FloatingWindowBase
    {
        public DocumentWindow(TabTreeContext context)
        {
            Context = context;
            Content = WithMarkers(TreeSlot);
        }

        public TabTreeContext Context { get; }

        public Decorator TreeSlot { get; } = new() { Name = "PART_DocumentTree" };

        /// <summary>Tabs projected last — the reconciliation key (DA-1.3).</summary>
        public HashSet<string> Tabs { get; } = new(StringComparer.Ordinal);

        public override void ReleaseContent()
        {
            if (TreeSlot.Child is Control view)
            {
                TabTreeContext.ReleaseHosts(view);
            }

            TreeSlot.Child = null;
        }
    }
}
