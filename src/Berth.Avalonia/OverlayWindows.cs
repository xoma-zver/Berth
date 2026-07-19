using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// Materialization of the floating layer on a platform without real windows (TW-7.7,
/// DA-7.5): open Float tool windows — and Window ones degraded by TW-7.6 — become
/// pseudo-windows in the workspace overlay canvas hosting the same cached
/// <see cref="ToolWindowDecorator"/>, and document windows become pseudo-windows projecting
/// their tab trees over the workspace-wide host cache. Reconciliation mirrors the desktop
/// <see cref="FloatingWindowLayer"/>: panels by id, document windows by tab-set overlap.
/// Window gestures reduce to core commands: moving and resizing are pure visualization until
/// the release, which commits one bounds command — Esc cancels without a command; the
/// document title bar's «×» issues one CloseTab per tab. The «screen» is the workspace:
/// bounds live in workspace coordinates, and the render clamps a pseudo-window into the
/// workspace without touching the state. Z-order is the canvas child order: a press or focus
/// gain inside raises the pseudo-window and is never persisted.
/// </summary>
internal sealed class OverlayWindowLayer : IFloatingLayer
{
    private readonly BerthWorkspace _workspace;
    private readonly Dictionary<string, PseudoWindow> _panels = new(StringComparer.Ordinal);
    private readonly List<PseudoWindow> _documents = [];
    private bool _torndown;

    public OverlayWindowLayer(BerthWorkspace workspace) => _workspace = workspace;

    /// <inheritdoc/>
    public bool IsWindowed => false;

    /// <inheritdoc/>
    public void Update(LayoutState state, ToolWindowRegistry registry)
    {
        if (_torndown || _workspace.PseudoWindowLayer is not { } canvas)
        {
            return;
        }

        UpdatePanels(canvas, state);
        UpdateDocuments(canvas, state, registry);
    }

    /// <inheritdoc/>
    public void Teardown()
    {
        if (_torndown)
        {
            return;
        }

        _torndown = true;
        var canvas = _workspace.PseudoWindowLayer;
        foreach (var panel in _panels.Values)
        {
            Remove(canvas, panel);
        }

        _panels.Clear();
        foreach (var document in _documents)
        {
            Remove(canvas, document);
        }

        _documents.Clear();
    }

    // ---- Float (and degraded Window) tool windows (TW-7.6, TW-7.7) ----

    private void UpdatePanels(Canvas canvas, LayoutState state)
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
                Remove(canvas, _panels[id]);
                _panels.Remove(id);
            }
        }

        foreach (var window in state.ToolWindows)
        {
            if (!window.IsOpen || window.Mode.GetLayer() != ToolWindowLayer.Floating)
            {
                continue;
            }

            // Float and a degraded Window host identically (TW-7.6: the effective mode of
            // both is Float here — GetEffectiveMode(canFloat: true, canUseWindowed: false));
            // a pseudo-window has no owned/independent distinction to make.
            if (!_panels.TryGetValue(window.Id, out var pseudo))
            {
                var id = window.Id;
                pseudo = new PseudoWindow(_workspace, id);
                pseudo.BoundsCommitted = bounds => CommitPanelBounds(id, bounds);
                _panels[id] = pseudo;
                canvas.Children.Add(pseudo); // arrival order; activation raises (TW-6.6)
                pseudo.ApplyBounds(window.FloatingBounds ?? _workspace.DefaultFloatingBounds());
            }
            else if (window.FloatingBounds is { } bounds && bounds != pseudo.AppliedBounds)
            {
                pseudo.ApplyBounds(bounds);
            }

            pseudo.SetContent(_workspace.GetHost(window.Id));
        }
    }

    private void CommitPanelBounds(string id, FloatingBounds bounds)
    {
        if (_torndown || !_panels.TryGetValue(id, out var pseudo))
        {
            return;
        }

        var current = _workspace.State?.ToolWindows.FirstOrDefault(
            w => string.Equals(w.Id, id, StringComparison.Ordinal));
        if (current is null || current.FloatingBounds == bounds)
        {
            return; // the equal-value guard breaks the command → projection loop
        }

        pseudo.AppliedBounds = bounds; // what we commit is what the window shows — no re-apply
        _workspace.Execute(s => s.SetFloatingBounds(id, bounds));
    }

    private static ToolWindowState? OpenFloating(LayoutState state, string id) =>
        state.ToolWindows.FirstOrDefault(w =>
            string.Equals(w.Id, id, StringComparison.Ordinal)
            && w.IsOpen
            && w.Mode.GetLayer() == ToolWindowLayer.Floating);

    // ---- document pseudo-windows (DA-7.5) ----

    private void UpdateDocuments(Canvas canvas, LayoutState state, ToolWindowRegistry registry)
    {
        var windows = state.DockArea.Windows;
        var matched = new PseudoWindow?[windows.Length];
        var used = new bool[_documents.Count];
        for (var i = 0; i < windows.Length; i++)
        {
            var tabs = DockTrees.TabsOf(windows[i].Root);
            for (var j = 0; j < _documents.Count; j++)
            {
                // Windows have no identity (DA-1.3): live pseudo-windows match state entries
                // by tab overlap, like groups in the tree reconciliation (DA-9.6).
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
                Remove(canvas, _documents[j]);
            }
        }

        _documents.Clear();
        for (var i = 0; i < windows.Length; i++)
        {
            var view = matched[i];
            if (view is null)
            {
                view = new PseudoWindow(_workspace, TabTreeContext.ForDocumentWindow(_workspace));
                var captured = view;
                view.BoundsCommitted = bounds => CommitDocumentBounds(captured, bounds);
                view.CloseRequested = () => CloseAllTabs(captured);
                canvas.Children.Add(view);
                view.ApplyBounds(windows[i].Bounds);
            }
            else if (windows[i].Bounds != view.AppliedBounds)
            {
                view.ApplyBounds(windows[i].Bounds);
            }

            view.Context!.DocumentWindowIndex = i;
            view.Tabs.Clear();
            view.Tabs.UnionWith(DockTrees.TabsOf(windows[i].Root));
            view.SetTitle(TabHostCache.TitleOf(_workspace, windows[i].CurrentTabId));
            view.Context.ReconcileRoot(view.TreeSlot!, windows[i].Root, state, registry);
            _documents.Add(view);
        }
    }

    /// <summary>
    /// The «×» of a document pseudo-window: one CloseTab per tab (DA-7.3) — a UI composition
    /// over CloseTab, one command with one lifecycle report each; the emptied window then
    /// disappears from the state (INV-D6) and the projection removes the pseudo-window.
    /// </summary>
    private void CloseAllTabs(PseudoWindow view)
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

    private void CommitDocumentBounds(PseudoWindow view, FloatingBounds bounds)
    {
        if (_torndown || _workspace.State is not { } state)
        {
            return;
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

    // ---- shared plumbing ----

    /// <summary>Returns the hosted content to the workspace caches and drops the pseudo-window.</summary>
    private static void Remove(Canvas? canvas, PseudoWindow pseudo)
    {
        pseudo.ReleaseContent();
        canvas?.Children.Remove(pseudo);
    }
}

/// <summary>
/// One pseudo-window of the overlay layer (TW-7.7, DA-7.5): an opaque bordered surface
/// positioned on the overlay canvas. A panel pseudo-window hosts the cached
/// <see cref="ToolWindowDecorator"/> directly — the decorator header is the move handle
/// (delegated by the decorator, TW-7.7) and its «—» already closes (TW-7.3), so there is no
/// extra title bar; a document pseudo-window carries its own title bar (title + «×») above the
/// projected tab tree (DA-7.5). The frame band around the content is the resize handle. Moving
/// and resizing are pure visualization until the release (the TW-5.17 style): live updates
/// touch only the canvas position and size, the release after actual movement invokes
/// <see cref="BoundsCommitted"/> once, Esc restores the starting rectangle without a command,
/// and a lost capture cancels. A press or focus gain anywhere inside raises the pseudo-window
/// in z-order (TW-6.6); a header click that never became a move focuses the panel content on
/// release — the deferred activation of TW-6.6.
/// </summary>
internal sealed class PseudoWindow : Border
{
    /// <summary>Thickness of the resize band along the edges, overlapping the content margin.</summary>
    private const double ResizeBand = 6;

    /// <summary>Minimum pseudo-window size on resize (render-side constant, TW-2.8).</summary>
    private const double MinSize = BerthMetrics.MinPaneSize;

    /// <summary>Minimum visible part of a pseudo-window kept inside the workspace by the render clamp (TW-7.7).</summary>
    private const double MinVisibleEdge = 48;

    private readonly BerthWorkspace _workspace;
    private readonly Decorator _contentSlot = new();
    private readonly string? _panelId;
    private readonly TextBlock? _titleText;

    private bool _gestureActive;
    private bool _gestureMoved;
    private bool _gestureCancelled;
    private bool _gestureIsHeaderClickCandidate;
    private Point _gestureStartPointer;
    private Rect _gestureStartRect;
    private int _resizeX; // -1 left edge, 0 none, 1 right edge
    private int _resizeY; // -1 top edge, 0 none, 1 bottom edge
    private TopLevel? _gestureTopLevel;
    private PanelDockGuide? _dockGuide; // stripe dock targets of a panel header move (TW-7.7 ext)

    private PseudoWindow(BerthWorkspace workspace)
    {
        _workspace = workspace;
        Name = "PART_PseudoWindow";
        BorderBrush = BerthBrushes.Separator;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(ResizeBand - 1);
        ActualThemeVariantChanged += (_, _) => UpdateSurface();
        UpdateSurface();
        // handledEventsToo: the workspace drag controller marks bare tab-header presses
        // handled earlier on the same tunnel (the press-focus deferral of DA-9.7), and such a
        // press must still raise the pseudo-window (TW-6.6, task 6.2).
        AddHandler(PointerPressedEvent, OnPreviewPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => BringToFront(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Panel pseudo-window: hosts the cached decorator of the tool window (TW-7.7).</summary>
    public PseudoWindow(BerthWorkspace workspace, string panelId)
        : this(workspace)
    {
        _panelId = panelId;
        Child = _contentSlot;
    }

    /// <summary>Document pseudo-window: own title bar over the projected tab tree (DA-7.5).</summary>
    public PseudoWindow(BerthWorkspace workspace, TabTreeContext context)
        : this(workspace)
    {
        Context = context;
        TreeSlot = new Decorator { Name = "PART_DocumentTree" };
        _titleText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var close = new Button
        {
            Name = "PART_PseudoWindowClose",
            Content = "×",
            Focusable = false,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            BorderThickness = default,
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => CloseRequested?.Invoke();
        DockPanel.SetDock(close, Dock.Right);
        var titleRow = new DockPanel { Height = BerthMetrics.HeaderHeight };
        titleRow.Children.Add(close);
        titleRow.Children.Add(_titleText);
        var titleBar = new Border
        {
            Name = "PART_PseudoWindowTitle",
            Child = titleRow,
            Background = BerthBrushes.Pane,
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        // The title bar is the move handle (DA-7.5); the «×» keeps its own press.
        titleBar.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !IsPressOnButton(e.Source))
            {
                BeginMove(e);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        DockPanel.SetDock(titleBar, Dock.Top);
        var root = new DockPanel();
        root.Children.Add(titleBar);
        root.Children.Add(TreeSlot);
        Child = root;
    }

    /// <summary>Id of the hosted tool window, or null for a document pseudo-window.</summary>
    public string? PanelId => _panelId;

    /// <summary>Tree context of a document pseudo-window; null for a panel one.</summary>
    public TabTreeContext? Context { get; }

    /// <summary>Slot the document tree projects into; null for a panel pseudo-window.</summary>
    public Decorator? TreeSlot { get; }

    /// <summary>Tabs projected last — the reconciliation key of a document pseudo-window (DA-1.3).</summary>
    public HashSet<string> Tabs { get; } = new(StringComparer.Ordinal);

    /// <summary>Last bounds applied from the state or committed; an equal state value is not re-applied.</summary>
    public FloatingBounds? AppliedBounds { get; set; }

    /// <summary>Invoked once per completed move/resize gesture with the final bounds (TW-5.9, DA-5.8).</summary>
    public Action<FloatingBounds>? BoundsCommitted { get; set; }

    /// <summary>Invoked by the document title bar's «×» (DA-7.3); null for a panel pseudo-window.</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>Reattaches the cached host into the panel pseudo-window (TW-9.13); a no-op for the same host.</summary>
    public void SetContent(Control host)
    {
        if (ReferenceEquals(_contentSlot.Child, host))
        {
            return;
        }

        if (_contentSlot.Child is { } previous)
        {
            BerthWorkspace.DetachFromParent(previous);
        }

        BerthWorkspace.DetachFromParent(host);
        _contentSlot.Child = host;
    }

    /// <summary>Returns the hosted content to the workspace caches before the pseudo-window goes away.</summary>
    public void ReleaseContent()
    {
        if (_contentSlot.Child is { } host)
        {
            BerthWorkspace.DetachFromParent(host);
        }

        if (TreeSlot?.Child is Control view)
        {
            TabTreeContext.ReleaseHosts(view);
            TreeSlot.Child = null;
        }
    }

    /// <summary>Title of a document pseudo-window — the current tab's display string (DA-7.5).</summary>
    public void SetTitle(string title)
    {
        if (_titleText is not null)
        {
            _titleText.Text = title;
        }
    }

    /// <summary>
    /// Applies state bounds (workspace coordinates, TW-7.7). The visual position is clamped
    /// into the workspace so a grabbable part stays reachable — a render clamp that never
    /// touches the state (TW-2.8): <see cref="AppliedBounds"/> keeps the state value, so the
    /// equal-value guards still hold.
    /// </summary>
    public void ApplyBounds(FloatingBounds bounds)
    {
        AppliedBounds = bounds;
        var rect = ClampToCanvas(new Rect(bounds.X, bounds.Y, Math.Max(MinSize, bounds.Width), Math.Max(MinSize, bounds.Height)));
        Canvas.SetLeft(this, rect.X);
        Canvas.SetTop(this, rect.Y);
        Width = rect.Width;
        Height = rect.Height;
    }

    /// <summary>Raises the pseudo-window to the top of the overlay canvas (TW-6.6); z-order is never persisted (DA-7.1).</summary>
    public void BringToFront()
    {
        if (Parent is Canvas canvas)
        {
            var index = canvas.Children.IndexOf(this);
            if (index >= 0 && index != canvas.Children.Count - 1)
            {
                canvas.Children.Move(index, canvas.Children.Count - 1);
            }
        }
    }

    /// <summary>
    /// Starts the move gesture — called by the document title bar, or by the hosted decorator
    /// delegating its bare header press (TW-7.7): inside a pseudo-window the header drags the
    /// window, not the slot-drag of TW-5.17.
    /// </summary>
    public void BeginMove(PointerPressedEventArgs e) => BeginGesture(e, resizeX: 0, resizeY: 0);

    // ---- gesture machinery ----

    private void OnPreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        BringToFront();
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _gestureActive)
        {
            return; // a handled press — a deferred tab-header press — only raises the window
        }

        var position = e.GetPosition(this);
        var x = position.X <= ResizeBand ? -1 : position.X >= Bounds.Width - ResizeBand ? 1 : 0;
        var y = position.Y <= ResizeBand ? -1 : position.Y >= Bounds.Height - ResizeBand ? 1 : 0;
        if (x != 0 || y != 0)
        {
            BeginGesture(e, x, y);
            e.Handled = true; // the frame band wins over content underneath
        }
    }

    private void BeginGesture(PointerPressedEventArgs e, int resizeX, int resizeY)
    {
        _gestureActive = true;
        _gestureMoved = false;
        _gestureCancelled = false;
        _gestureIsHeaderClickCandidate = resizeX == 0 && resizeY == 0 && _panelId is not null;
        _resizeX = resizeX;
        _resizeY = resizeY;
        _gestureStartPointer = PointerInCanvas(e);
        _gestureStartRect = CurrentRect();
        e.Pointer.Capture(this);
        _gestureTopLevel = TopLevel.GetTopLevel(this);
        _gestureTopLevel?.AddHandler(KeyDownEvent, OnGestureKeyDown, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_gestureActive || _gestureCancelled || !ReferenceEquals(e.Pointer.Captured, this))
        {
            return;
        }

        var delta = PointerInCanvas(e) - _gestureStartPointer;
        if (delta == default && !_gestureMoved)
        {
            return;
        }

        _gestureMoved = true;
        var rect = _gestureStartRect;
        if (_resizeX == 0 && _resizeY == 0)
        {
            rect = rect.WithX(rect.X + delta.X).WithY(rect.Y + delta.Y);
        }
        else
        {
            var left = rect.X + (_resizeX < 0 ? Math.Min(delta.X, rect.Width - MinSize) : 0);
            var top = rect.Y + (_resizeY < 0 ? Math.Min(delta.Y, rect.Height - MinSize) : 0);
            var width = Math.Max(MinSize, rect.Width + (_resizeX < 0 ? -delta.X : _resizeX > 0 ? delta.X : 0));
            var height = Math.Max(MinSize, rect.Height + (_resizeY < 0 ? -delta.Y : _resizeY > 0 ? delta.Y : 0));
            rect = new Rect(left, top, width, height);
        }

        SetVisualRect(ClampToCanvas(rect)); // pure visualization until the release (TW-7.7)

        if (_gestureIsHeaderClickCandidate)
        {
            // A panel header move offers docking (TW-7.7 ext): light the stripe zones under
            // the pointer, unless Ctrl parks the window at the edge. The guide is built on the
            // first real movement, so a mere header click pays nothing.
            _dockGuide ??= _workspace.BeginPanelDockGuide(_panelId!);
            _dockGuide?.Update(PointerInCanvas(e), suppressed: e.KeyModifiers.HasFlag(KeyModifiers.Control));
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_gestureActive)
        {
            return;
        }

        var moved = _gestureMoved && !_gestureCancelled;
        var headerClick = _gestureIsHeaderClickCandidate && !_gestureMoved && !_gestureCancelled;
        // A release over a stripe zone docks the panel instead of moving it (TW-7.7 ext),
        // unless Ctrl parks it at the edge; resolved before EndGesture drops the guide.
        var dockTarget = moved && !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            ? _dockGuide?.Resolve(PointerInCanvas(e))
            : null;
        EndGesture();
        e.Pointer.Capture(null);
        if (dockTarget is { } target)
        {
            // The same Move + SetMode(LastInternalMode) command as the reverse icon/header
            // drop (TW-7.8), through the workspace funnel. Deferred: the docking re-projection
            // removes this pseudo-window, and that removal must not run inside its own pointer
            // event (the non-reentrant close precedent of the floating layer).
            Dispatcher.UIThread.Post(() => target.Commit(_workspace));
        }
        else if (moved)
        {
            var rect = CurrentRect();
            // One command per completed gesture (TW-5.9, DA-5.8); a click without movement
            // commits nothing — the splitter precedent.
            BoundsCommitted?.Invoke(new FloatingBounds(rect.X, rect.Y, rect.Width, rect.Height));
        }
        else if (headerClick && !IsKeyboardFocusWithin)
        {
            // The deferred header activation of TW-6.6: a header click that never became a
            // window move focuses the panel content, activating the panel by DA-6.4 wiring.
            (_contentSlot.Child as ToolWindowDecorator)?.FocusContent();
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_gestureActive)
        {
            CancelGesture();
            EndGesture();
        }
    }

    private void OnGestureKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _gestureActive && !_gestureCancelled)
        {
            // Esc restores the starting rectangle without a command (TW-7.7); the capture
            // holds until the release, which then does nothing.
            CancelGesture();
            e.Handled = true;
        }
    }

    private void CancelGesture()
    {
        _gestureCancelled = true;
        _dockGuide?.Hide();
        SetVisualRect(_gestureStartRect);
    }

    private void EndGesture()
    {
        _gestureActive = false;
        _dockGuide?.Hide();
        _dockGuide = null;
        _gestureTopLevel?.RemoveHandler(KeyDownEvent, OnGestureKeyDown);
        _gestureTopLevel = null;
    }

    private Rect CurrentRect() => new(Canvas.GetLeft(this), Canvas.GetTop(this), Bounds.Width, Bounds.Height);

    private void SetVisualRect(Rect rect)
    {
        Canvas.SetLeft(this, rect.X);
        Canvas.SetTop(this, rect.Y);
        Width = rect.Width;
        Height = rect.Height;
    }

    private Point PointerInCanvas(PointerEventArgs e) =>
        Parent is Canvas canvas ? e.GetPosition(canvas) : e.GetPosition(this);

    /// <summary>
    /// The render clamp of TW-7.7: at least <see cref="MinVisibleEdge"/> of the pseudo-window
    /// stays horizontally inside the workspace and the grab row stays vertically reachable.
    /// Skipped before the canvas has a layout (size zero).
    /// </summary>
    private Rect ClampToCanvas(Rect rect)
    {
        if (Parent is not Canvas canvas || canvas.Bounds.Width <= 0 || canvas.Bounds.Height <= 0)
        {
            return rect;
        }

        var x = Math.Clamp(rect.X, MinVisibleEdge - rect.Width, canvas.Bounds.Width - MinVisibleEdge);
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, canvas.Bounds.Height - MinVisibleEdge));
        return new Rect(x, y, rect.Width, rect.Height);
    }

    private static bool IsPressOnButton(object? source)
    {
        for (var node = source as Visual; node is not null; node = node.GetVisualParent())
        {
            if (node is Button)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The skeleton brushes are translucent; a pseudo-window needs an opaque surface (TW-7.7: nothing shows through).</summary>
    private void UpdateSurface() => Background = ActualThemeVariant == ThemeVariant.Dark
        ? BerthBrushes.DarkOverlaySurface
        : BerthBrushes.LightOverlaySurface;
}
