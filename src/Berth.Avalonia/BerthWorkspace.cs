using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// Root control materializing a <see cref="LayoutState"/>: the stripes at the left and right
/// edges, the side panes and the dock area between them, the Undock overlay above the docked
/// layout, and the floating layer — real OS windows under a desktop <see cref="Window"/>,
/// pseudo-windows in the workspace overlay elsewhere (the browser). The dock area and the
/// content trees of open tool windows materialize their tab trees by one shared projection
/// over a single workspace-wide tab-host cache, so a move between a panel and the dock area
/// reattaches the same host with its built view.
///
/// The control is a pure projection of the state: fractions become pixels here, render-time
/// minimums clamp without touching the state, and materialization is incremental — hosts of
/// surviving windows and tabs update in place, so keyboard focus and view-state are never
/// lost to materialization itself; leaf chrome (stripe buttons, tab headers, menus,
/// splitters) is rebuilt per update. Input reduces to core commands: every completed gesture
/// applies one command and assigns the result back to <see cref="State"/> — observe
/// user-driven changes with <c>GetObservable(StateProperty)</c>. Activation transfers
/// keyboard focus into the activated window's content, focus gains inside a window activate
/// it, and DockUnpinned/Undock windows auto-hide on focus loss and on outside clicks. The
/// application binds its keymap to <see cref="ActivateToolWindow"/> and may supply
/// <see cref="ShortcutHintProvider"/> to show the shortcuts in stripe icon tooltips.
///
/// With a <see cref="Lifecycle"/> attached, tab content materializes lazily through the
/// coordinator's pull pass and every gesture command reports its transition to it. Registry
/// mutations are invisible to the property system — call <see cref="Refresh"/> after live
/// registrations. Replacing <see cref="Registry"/> or <see cref="Lifecycle"/> is a full
/// reconfiguration: the skeleton and the host cache are rebuilt from scratch and retained
/// views are dropped.
/// </summary>
public sealed class BerthWorkspace : Decorator
{
    /// <summary>Defines the <see cref="State"/> property.</summary>
    public static readonly StyledProperty<LayoutState?> StateProperty =
        AvaloniaProperty.Register<BerthWorkspace, LayoutState?>(nameof(State));

    /// <summary>Defines the <see cref="Registry"/> property.</summary>
    public static readonly StyledProperty<ToolWindowRegistry?> RegistryProperty =
        AvaloniaProperty.Register<BerthWorkspace, ToolWindowRegistry?>(nameof(Registry));

    /// <summary>Defines the <see cref="Lifecycle"/> property.</summary>
    public static readonly StyledProperty<ContentLifecycle?> LifecycleProperty =
        AvaloniaProperty.Register<BerthWorkspace, ContentLifecycle?>(nameof(Lifecycle));

    /// <summary>Defines the <see cref="Definition"/> property.</summary>
    public static readonly StyledProperty<BerthLayoutDefinition?> DefinitionProperty =
        AvaloniaProperty.Register<BerthWorkspace, BerthLayoutDefinition?>(nameof(Definition));

    /// <summary>Defines the <see cref="ShortcutHintProvider"/> property.</summary>
    public static readonly StyledProperty<Func<string, string?>?> ShortcutHintProviderProperty =
        AvaloniaProperty.Register<BerthWorkspace, Func<string, string?>?>(nameof(ShortcutHintProvider));

    /// <summary>Defines the <see cref="TabTitleProvider"/> property.</summary>
    public static readonly StyledProperty<Func<string, string?>?> TabTitleProviderProperty =
        AvaloniaProperty.Register<BerthWorkspace, Func<string, string?>?>(nameof(TabTitleProvider));

    /// <summary>Defines the <see cref="WindowTitleSuffix"/> property.</summary>
    public static readonly StyledProperty<string?> WindowTitleSuffixProperty =
        AvaloniaProperty.Register<BerthWorkspace, string?>(nameof(WindowTitleSuffix));

    private readonly Dictionary<string, ToolWindowDecorator> _hosts = new(StringComparer.Ordinal);
    private readonly List<TopLevel> _windowZOrder = [];
    private TabHostCache? _tabHosts;
    private ToolWindowStripe? _leftStripe;
    private ToolWindowStripe? _rightStripe;
    private WorkspaceGrid? _grid;
    private UndockOverlay? _overlay;
    private DockAreaView? _dockView;
    private DragLayer? _dragLayer;
    private Canvas? _pseudoLayer;
    private DragController? _drag;
    private AutoHideController? _autoHide;
    private IFloatingLayer? _floating;
    private bool _definitionApplied;

    /// <summary>
    /// The single tab-host cache of the workspace (DA-9.6): shared by the dock area and
    /// the panel trees, so a move between hosts reattaches the same host with its built view.
    /// </summary>
    internal TabHostCache TabHosts => _tabHosts ??= new TabHostCache(this);

    /// <summary>The drag gesture controller (TW-5.17); null until the skeleton is built.</summary>
    internal DragController? Drag => _drag;

    /// <summary>
    /// TopLevels of the workspace in approximate z-order, topmost first (TW-5.17, task
    /// 6.2): floating windows above the main window, ordered among themselves by their last
    /// activations, a newly shown one on top — the approximation of the OS z-order, which
    /// the platform does not expose (a v1 assumption). The drag hit-test resolves the top
    /// window at a point over this order.
    /// </summary>
    internal IReadOnlyList<TopLevel> WindowsTopMostFirst => _windowZOrder;

    /// <summary>Floating TopLevels of the workspace — every registered window except the main one.</summary>
    internal IEnumerable<TopLevel> FloatingRoots
    {
        get
        {
            var main = TopLevel.GetTopLevel(this);
            foreach (var topLevel in _windowZOrder)
            {
                if (!ReferenceEquals(topLevel, main))
                {
                    yield return topLevel;
                }
            }
        }
    }

    /// <summary>The layout to materialize; null renders nothing.</summary>
    public LayoutState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Registrations supplying titles, icons and content factories; null renders nothing.</summary>
    public ToolWindowRegistry? Registry
    {
        get => GetValue(RegistryProperty);
        set => SetValue(RegistryProperty, value);
    }

    /// <summary>
    /// Optional content coordinator. When set, tab content — documents, panel tabs and panel
    /// bodies — materializes lazily through <see cref="ContentLifecycle.MaterializeTab"/> in a
    /// pull pass outside the projection: a <see cref="Control"/> content object is hosted
    /// directly, anything else gets its view built once by the application's data templates
    /// and hosted for the content's lifetime. Every gesture command reports its transition to
    /// <see cref="ContentLifecycle.NotifyTransition"/> — exactly one call per command;
    /// transitions the application performs itself — direct <see cref="State"/> assignments,
    /// Apply, ResetToDefaults — remain the application's to report. Null keeps the static
    /// skeleton: tabs stay placeholders.
    /// </summary>
    public ContentLifecycle? Lifecycle
    {
        get => GetValue(LifecycleProperty);
        set => SetValue(LifecycleProperty, value);
    }

    /// <summary>
    /// Optional markup-declared default composition: when set while
    /// <see cref="Registry"/> and <see cref="Lifecycle"/> are unset, the workspace builds the
    /// definition on its first attach and assigns the resulting composition to its own
    /// <see cref="Registry"/>, <see cref="Lifecycle"/> and <see cref="State"/> — the zero-code
    /// path of a markup-only application; <see cref="State"/> stays observable and bindable as
    /// usual, so persistence wires on top unchanged. Explicit properties win: with a Registry
    /// or Lifecycle already set the definition is ignored with a trace. The definition is
    /// applied once; replacing it after application is likewise ignored — a full
    /// reconfiguration is an explicit Registry/Lifecycle swap.
    /// </summary>
    public BerthLayoutDefinition? Definition
    {
        get => GetValue(DefinitionProperty);
        set => SetValue(DefinitionProperty, value);
    }

    /// <summary>
    /// Optional application-supplied shortcut hints: maps a tool window id to the display
    /// string of its activation shortcut, which extends the stripe icon tooltip beyond the
    /// title. The shortcuts themselves are the application's keymap — bind them to
    /// <see cref="ActivateToolWindow"/>; the provider only supplies what to display. Null —
    /// the provider or its result for an id — leaves the tooltip as the bare title.
    /// </summary>
    public Func<string, string?>? ShortcutHintProvider
    {
        get => GetValue(ShortcutHintProviderProperty);
        set => SetValue(ShortcutHintProviderProperty, value);
    }

    /// <summary>
    /// Optional application-supplied tab titles: maps a tab id to the display string of its
    /// header and placeholder. The layout stores identifiers only, and a title must exist
    /// before content materializes and for sleeping tabs, so titles come from the application,
    /// not from the content. Null — the provider or its result for an id — falls back to the
    /// id itself.
    /// </summary>
    public Func<string, string?>? TabTitleProvider
    {
        get => GetValue(TabTitleProviderProperty);
        set => SetValue(TabTitleProviderProperty, value);
    }

    /// <summary>
    /// Optional application-supplied suffix of independent window titles: appended as
    /// «{Title} - {suffix}» to Window-mode tool windows and to document windows — the
    /// independent top-levels living in the OS taskbar and Alt-Tab. The library has no notion
    /// of a project, so the name comes from the application — typically the open project or
    /// solution. Owned Float windows never carry the suffix; null or empty leaves every title
    /// bare.
    /// </summary>
    public string? WindowTitleSuffix
    {
        get => GetValue(WindowTitleSuffixProperty);
        set => SetValue(WindowTitleSuffixProperty, value);
    }

    /// <summary>
    /// The activation shortcut of one tool window — the public entry the application binds
    /// its keymap to: a closed window opens and activates, an open inactive one activates, an
    /// open active one closes. «Active» is decided by keyboard focus lying logically within
    /// the window or its stripe icon, not by the stored
    /// <see cref="LayoutState.ActiveToolWindowId"/> — the field lags behind focus, and a check
    /// against it would close a window without focus. Runs through the command channel:
    /// activation transfers focus into the content and the transition is reported to
    /// <see cref="Lifecycle"/> once. A no-op without a state; an id absent from the layout is
    /// a caller error, as in the core operations.
    /// </summary>
    /// <param name="id">Id of a tool window present in the layout.</param>
    /// <exception cref="ArgumentException">The id is empty, or no such tool window exists in the layout.</exception>
    public void ActivateToolWindow(string id)
    {
        // TW-5.5 (tri-state logic by focus), TW-6.6 (focus transfer), TW-6.1 (auto-hide stays
        // reachable through the command channel).
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (State is not { } state)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var close = AutoHideController.IsWithinPanel(focused as ILogical, id) && IsOpenIn(state, id);
        Execute(s => close ? s.Close(id) : s.Open(id));
        if (!close)
        {
            // Open of an already open window only re-activates (TW-5.2): the stored active id
            // may not change — a stale «last active» before phase 4 — while the shortcut's
            // activation must still move focus (TW-6.6). A no-op when Execute already did.
            FocusActivated(id);
        }
    }

    /// <summary>
    /// Projects the current <see cref="State"/> and <see cref="Registry"/> again. Required
    /// after operations that mutate the registry in place — the live registration lifecycle
    /// (<see cref="ContentLifecycle.Register"/>, RegisterDockContent, Unregister): they may
    /// return a state value-equal to the current one while titles, icons and claims changed,
    /// and the property system deduplicates equal assignments. Sleeping dock tabs re-resolve
    /// their owners on every projection, so a refresh after a live registration also wakes
    /// them up. Core layout commands need no explicit refresh beyond assigning their result
    /// to <see cref="State"/>.
    /// </summary>
    public void Refresh() => Sync();

    /// <summary>
    /// Applies one core command to the live <see cref="State"/> and assigns the result back —
    /// the single funnel of every completed input gesture (ADR-0004). A no-op without a state.
    /// One gesture is one command, so the transition report satisfies the one-call-per-operation
    /// contract of <see cref="ContentLifecycle.NotifyTransition"/> by construction (TW-9.2).
    /// </summary>
    internal void Execute(Func<LayoutState, LayoutState> command)
    {
        if (State is { } state)
        {
            // Captured before the command: the whitelisted reattachment of TW-9.13 below
            // loses keyboard focus as a side effect, so only the pre-command position can
            // tell whether the focus belonged to the moved window.
            var focusedBefore = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            // Resolve the owning host now, while the tree is intact: closing a DisposeOnClose
            // panel releases (orphans) its focused content during the assignment below, after
            // which the ancestry can no longer be walked — the DA-E21 focus return would be lost.
            var focusedVisual = focusedBefore as Visual;
            var tabHostBefore = focusedVisual?.FindAncestorOfType<DockTabHost>(includeSelf: true);
            var panelBefore = focusedVisual?.FindAncestorOfType<ToolWindowDecorator>(includeSelf: true);
            var result = command(state);
            // Assign first: the sync detaches released content from the visual tree before
            // the coordinator hands it back to its factory.
            State = result;
            Lifecycle?.NotifyTransition(state, result);
            // The focus transfer of activation (TW-6.6) runs last: a nested command raised
            // by the focus change — the auto-hide close of the focus loser (TW-6.1) — then
            // reports its own transition after this one, in gesture order. Recursion is
            // finite: the nested commands never activate another window.
            if (result.ActiveToolWindowId is { } active)
            {
                if (!string.Equals(active, state.ActiveToolWindowId, StringComparison.Ordinal))
                {
                    FocusActivated(active);
                }
                else if (WasReattached(state, result, active)
                    && AutoHideController.IsWithinPanel(focusedBefore as ILogical, active))
                {
                    // The extended trigger of TW-6.6: the command factually reattached the
                    // active window's host (another slot or layer, TW-9.13), dropping the
                    // keyboard focus it held — restore it into the content it left. Focus
                    // that was outside the window stays with its owner.
                    FocusActivated(active);
                }
            }

            RestoreDockFocus(focusedBefore, tabHostBefore, panelBefore, state, result);
        }
    }

    /// <summary>
    /// The dock side of the focus contract (DA-9.6, DA-E21, TW-6.3): when the command
    /// left keyboard focus dangling — the focused element was detached together with its
    /// host — the command channel restores it: into the same tab's content after a legal
    /// reattachment (the mirror of TW-6.6), or into the current tab of the effective active
    /// host when the focused tab or tool window is gone. Focus held by a live owner is never
    /// stolen; direct <see cref="State"/> assignments do not pass here (DA-6.4).
    /// </summary>
    private void RestoreDockFocus(
        IInputElement? focusedBefore,
        DockTabHost? tabHostBefore,
        ToolWindowDecorator? panelBefore,
        LayoutState before,
        LayoutState after)
    {
        if (_tabHosts is null || focusedBefore is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var focusedNow = topLevel?.FocusManager?.GetFocusedElement();
        if (focusedNow is not null && !ReferenceEquals(focusedNow, topLevel))
        {
            return; // not dangling: the focus belongs to a live owner
        }

        if (tabHostBefore is { } tabHost)
        {
            if (_tabHosts.TryFocusTab(tabHost.TabId))
            {
                return;
            }

            // The focused tab is gone or detached. Inside a still-open panel the focus stays
            // with the owner — its group's new active tab (DA-5.2): a jump to the dock area
            // would deactivate the still-active panel and auto-hide an unpinned one (TW-6.1).
            // Otherwise — the tab closed together with its panel, or it was a dock tab — the
            // dock area's current tab takes the focus (DA-6.3, DA-E21).
            if (panelBefore is { } owner && IsOpenIn(after, owner.ToolWindowId))
            {
                owner.FocusContent();
                return;
            }

            _tabHosts.TryFocusTab(CurrentTabOfActiveHost(after));
            return;
        }

        if (panelBefore is { } panel
            && IsOpenIn(before, panel.ToolWindowId)
            && !IsOpenIn(after, panel.ToolWindowId))
        {
            // The command closed the focused tool window: focus returns to the document
            // (DA-E21 — the shortcut close, the hide button, an outside-click close). The
            // owning host was resolved before the command, so a released DisposeOnClose body
            // (orphaned by the close) does not lose the return.
            _tabHosts.TryFocusTab(CurrentTabOfActiveHost(after));
        }
    }

    /// <summary>
    /// Focuses the current tab of the effective active host (TW-6.3, DA-E21): the active
    /// dock host when its window is materialized — a real window or a pseudo-window — else
    /// the main window: a stored ActiveDockHost pointing at a document window degrades in
    /// presentation only on a platform where document windows never materialize (DA-6.4).
    /// False without a target — the empty main window (TW-6.3: Esc without a target is a
    /// no-op).
    /// </summary>
    internal bool FocusCurrentDockTab() =>
        State is { } state && _tabHosts?.TryFocusTab(CurrentTabOfActiveHost(state)) == true;

    /// <summary>
    /// Current tab of the effective active host (DA-6.4): the active dock host's own
    /// current tab when document windows are materialized (real or pseudo, tasks 6.0/6.1),
    /// else the main window's — the presentation degradation of a platform without either.
    /// </summary>
    private string? CurrentTabOfActiveHost(LayoutState state) =>
        _floating is not null
            && state.DockArea.ActiveDockHost.DocumentWindowIndex is { } index
            && index < state.DockArea.Windows.Length
        ? state.DockArea.Windows[index].CurrentTabId
        : state.DockArea.CurrentTabId;

    /// <summary>The focus transfer of a tab gesture (DA-6.4); a no-op when the tab has no attached host.</summary>
    internal void FocusTab(string id) => _tabHosts?.TryFocusTab(id);

    /// <summary>
    /// The focus transfer of a completed float take-out (TW-7.8): the same rules as
    /// command activation (TW-6.6) — a no-op when the focus is already inside the window or
    /// the host is not attached. Needed explicitly because the activating Open of an already
    /// active window does not change the stored active id, so the command funnel alone would
    /// not transfer focus.
    /// </summary>
    internal void FocusToolWindow(string id) => FocusActivated(id);

    /// <summary>
    /// Whether the command factually reattached the window's host: open before and after with
    /// the slot or the layer changed — the reattachment whitelist of TW-9.13
    /// (DockPinned ↔ DockUnpinned changes neither and never touches the host).
    /// </summary>
    private static bool WasReattached(LayoutState before, LayoutState after, string id)
    {
        var was = Find(before, id);
        var now = Find(after, id);
        return was is { IsOpen: true }
            && now is { IsOpen: true }
            && (was.Slot != now.Slot || was.Mode.GetLayer() != now.Mode.GetLayer());

        static ToolWindowState? Find(LayoutState state, string id) =>
            state.ToolWindows.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));
    }

    private static bool IsOpenIn(LayoutState state, string id) =>
        state.ToolWindows.Any(w => string.Equals(w.Id, id, StringComparison.Ordinal) && w.IsOpen);

    /// <summary>
    /// Whether the window's content is hosted in the workspace layout: open in a docked or
    /// overlay mode (TW-3.2) — or in a floating mode while the floating layer is
    /// materialized (real windows or overlay pseudo-windows, tasks 6.0/6.1). The shared gate
    /// of the decorator projection, its tree materialization and the pull pass.
    /// </summary>
    internal bool IsHosted(ToolWindowState window) =>
        window.IsOpen && (window.Mode.GetLayer() != ToolWindowLayer.Floating || _floating is not null);

    /// <summary>
    /// Whether the platform hosts the Float mode: true while the workspace is attached — as a
    /// real owned window on the desktop or as a pseudo-window in the workspace overlay
    /// elsewhere. Document windows materialize under the same condition. Read-only: the value
    /// reflects the platform, not a configuration. Menus are built from the capabilities; the
    /// stored <see cref="ToolWindowMode"/> never degrades — only the effective presentation
    /// does (<see cref="ToolWindowModeExtensions.GetEffectiveMode"/>).
    /// </summary>
    public bool CanFloat => _floating is not null; // TW-7.6/TW-7.7 capabilities

    /// <summary>
    /// Whether the platform hosts the Window mode — an independent top-level window: true
    /// while the workspace is attached under a real <see cref="Window"/> TopLevel; false in
    /// the browser, where a stored Window presents as an effective Float pseudo-window.
    /// Read-only, like <see cref="CanFloat"/>.
    /// </summary>
    public bool CanUseWindowed => _floating?.IsWindowed == true;

    /// <summary>
    /// Test seam: forces the overlay floating layer even under a Window TopLevel — the browser
    /// platform is unreachable in headless runs, whose TopLevel is always a Window. Read when
    /// the layer is created (attach, Reset); set before attaching.
    /// </summary>
    internal bool ForceOverlayFloating { get; set; }

    /// <summary>
    /// Test seam: forces the frameless Float presentation of TW-7.1 off Windows — the
    /// platform gate is unreachable in cross-platform headless runs. Read when the floating
    /// layer is created (attach, Reset); set before attaching.
    /// </summary>
    internal bool ForceFramelessFloat { get; set; }

    /// <summary>The pseudo-window canvas of the overlay floating layer (TW-7.7, DA-7.5); part of the skeleton.</summary>
    internal Canvas? PseudoWindowLayer => _pseudoLayer;

    /// <summary>
    /// Current bounds of a hosted tool window's content — what the UI passes to
    /// <see cref="LayoutOperations.SetMode"/> when a window enters Float/Window without saved
    /// bounds (TW-5.6): the core never invents pixels (ADR-0002). Screen coordinates on
    /// a platform with real windows; workspace coordinates on the overlay platform, whose
    /// «screen» is the workspace itself (TW-7.7). Null for a detached host.
    /// </summary>
    internal FloatingBounds? ScreenBoundsOf(string id)
    {
        if (!_hosts.TryGetValue(id, out var host)
            || host.Parent is null
            || !((ILogical)host).IsAttachedToLogicalTree)
        {
            return null;
        }

        if (_floating is { IsWindowed: true })
        {
            var origin = host.PointToScreen(default);
            return new FloatingBounds(origin.X, origin.Y, host.Bounds.Width, host.Bounds.Height);
        }

        var local = host.TranslatePoint(default, this);
        return local is { } point
            ? new FloatingBounds(point.X, point.Y, host.Bounds.Width, host.Bounds.Height)
            : null;
    }

    /// <summary>
    /// Default bounds of a new floating window when nothing better exists — the «Move to New
    /// Window» menu item and a floating record without saved bounds: the main window's
    /// rectangle inset on every side, no cascading (= the reference's programmatic
    /// suggestChildFrameBounds path — tracing DA-E-C4; owner decision, 2026-07). On the
    /// overlay platform — the workspace rectangle inset likewise, in workspace coordinates
    /// (TW-7.7, DA-7.5).
    /// </summary>
    internal FloatingBounds DefaultFloatingBounds()
    {
        if (_floating is { IsWindowed: true } && TopLevel.GetTopLevel(this) is Window main)
        {
            return FloatingBoundsValidation.DefaultRelativeTo(main);
        }

        if (_floating is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            return FloatingBoundsValidation.DefaultWithin(Bounds.Size);
        }

        return new FloatingBounds(100, 100, 400, 300);
    }

    /// <summary>
    /// The docked center area — the workspace between the stripes, holding the side panes,
    /// the dock area and the Undock overlay — in workspace coordinates; the geometry base of
    /// the post-drop zone preview (TW-5.17 v0.26, <see cref="DockZonePreview"/>). Null
    /// until the skeleton is built and laid out.
    /// </summary>
    internal Rect? DockedAreaRect()
    {
        if (_grid is null || _grid.Bounds.Width <= 0 || _grid.Bounds.Height <= 0)
        {
            return null;
        }

        return _grid.TranslatePoint(default, this) is { } origin
            ? new Rect(origin, _grid.Bounds.Size)
            : null;
    }

    /// <summary>
    /// Builds the dock assist of a panel window's live move gesture (TW-7.7 extension,
    /// TW-7.1): the stripe drop targets the panel may dock into, over the gesture space of the
    /// current floating layer — the workspace for a <see cref="PseudoWindow"/> header move on
    /// the overlay platform, screen coordinates for the frameless Float window of the windowed
    /// one. The marker paints through the matching gesture visual: the workspace
    /// <see cref="DragLayer"/> directly in the overlay space, the screen-to-workspace routing
    /// of <see cref="WindowedDragVisual"/> otherwise. Null when there is nothing to dock
    /// onto — no drag layer or no state. Called on the first movement of a header drag, so a
    /// mere header click pays nothing.
    /// </summary>
    internal PanelDockGuide? BeginPanelDockGuide(string panelId)
    {
        if (_dragLayer is not { } layer || State is not { } state)
        {
            return null;
        }

        // Zone geometry reads rendered stripe bounds: settle the layout first, as the drag
        // controller does before building its own catalog.
        UpdateLayout();
        var windowed = CanUseWindowed;
        var space = new DropZoneSpace(this, windowed);
        IDragVisual visual = windowed ? new WindowedDragVisual(this, layer) : new OverlayDragVisual(layer);
        return new PanelDockGuide(visual, StripeDropTargets.Build(this, state, panelId, space));
    }

    /// <summary>
    /// Registers a floating window's TopLevel with the shared wiring: focus and clicks
    /// (TW-6.1, TW-6.2, DA-6.4), drag sources and the z-order registry (TW-5.17, task 6.2) —
    /// a newly shown window enters the MRU order on top.
    /// </summary>
    internal void AttachFloatingTopLevel(TopLevel topLevel)
    {
        _autoHide?.Attach(topLevel);
        _drag?.AttachWindow(topLevel);
        RegisterTopLevel(topLevel, onTop: true);
    }

    /// <summary>Removes a closing floating window's TopLevel from the shared wiring.</summary>
    internal void DetachFloatingTopLevel(TopLevel topLevel)
    {
        _autoHide?.Detach(topLevel);
        _drag?.DetachWindow(topLevel);
        UnregisterTopLevel(topLevel);
    }

    private void RegisterTopLevel(TopLevel topLevel, bool onTop)
    {
        if (_windowZOrder.Contains(topLevel))
        {
            return;
        }

        if (onTop)
        {
            _windowZOrder.Insert(0, topLevel);
        }
        else
        {
            _windowZOrder.Add(topLevel);
        }

        if (topLevel is Window window)
        {
            window.Activated += OnWorkspaceWindowActivated;
        }
    }

    private void UnregisterTopLevel(TopLevel topLevel)
    {
        _windowZOrder.Remove(topLevel);
        if (topLevel is Window window)
        {
            window.Activated -= OnWorkspaceWindowActivated;
        }
    }

    /// <summary>
    /// The MRU update of the z-order approximation: an activated floating window moves on
    /// top. The main window stays at the back: owned Float windows are always above it on
    /// the OS, and a window shown without activation (a new document window) sits above the
    /// still-active main window too — activation events alone cannot tell those apart
    /// (the v1 assumption of TW-5.17).
    /// </summary>
    private void OnWorkspaceWindowActivated(object? sender, EventArgs e)
    {
        if (sender is TopLevel topLevel
            && !ReferenceEquals(topLevel, TopLevel.GetTopLevel(this))
            && _windowZOrder.Remove(topLevel))
        {
            _windowZOrder.Insert(0, topLevel);
        }
    }

    /// <summary>
    /// Activates the window hosting the visual, when it is a floating window of the workspace
    /// (TW-6.6, DA-6.4): a focus target in another OS window activates that window first
    /// (inert in headless runs, where every window reports active); a target inside a
    /// pseudo-window raises it in z-order — the overlay equivalent (TW-7.7).
    /// </summary>
    internal static void ActivateWindowOf(Visual target)
    {
        if (target.FindAncestorOfType<PseudoWindow>(includeSelf: true) is { } pseudo)
        {
            pseudo.BringToFront();
            return;
        }

        if (TopLevel.GetTopLevel(target) is Window window && !window.IsActive)
        {
            window.Activate();
        }
    }

    /// <summary>
    /// The focus transfer of TW-6.6: keyboard focus moves into the activated window's
    /// content — unless it is already inside (activation never re-arranges focus within the
    /// window) or the window has no attached host (a floating-mode record on a platform
    /// without real windows). A host living in a floating window activates its OS window
    /// first. Only the command funnel transfers focus: direct <see cref="State"/> assignments
    /// by the application do not pass here (TW-6.6).
    /// </summary>
    private void FocusActivated(string id)
    {
        // A parentless host is the detached cache entry of TW-9.13 — nothing to focus; the
        // parent is the attachment signal DetachFromParent maintains.
        if (!_hosts.TryGetValue(id, out var host) || host.Parent is null)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (AutoHideController.IsWithinPanel(focused as ILogical, id))
        {
            return;
        }

        ActivateWindowOf(host);
        host.FocusContent();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // At attach every markup property is set — the safe moment to self-assemble from a
        // declared Definition, whatever the XAML property order was.
        ApplyDefinition();
    }

    /// <summary>
    /// The one-shot self-assembly from <see cref="Definition"/>: builds the composition and
    /// assigns the trio, unless explicit properties already won. State is assigned last, so
    /// the projection first sees the complete configuration.
    /// </summary>
    private void ApplyDefinition()
    {
        if (_definitionApplied || Definition is not { } definition)
        {
            return;
        }

        _definitionApplied = true;
        if (Registry is not null || Lifecycle is not null)
        {
            Trace.TraceWarning(
                "Berth: the workspace already has an explicit Registry or Lifecycle; Definition is ignored — explicit properties win.");
            return;
        }

        var composition = definition.Build();
        Lifecycle = composition.Lifecycle;
        Registry = composition.Registry;
        State = composition.State;
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _autoHide = new AutoHideController(this);
            _autoHide.Attach(topLevel);
            RegisterTopLevel(topLevel, onTop: false); // the main window starts at the back
            _floating = CreateFloatingLayer(topLevel);

            // Re-project: the floating layer materializes the current state's floating
            // windows, and menus rebuilt before the attach did not offer the floating modes.
            Sync();
        }
    }

    /// <summary>
    /// Picks the floating layer implementation from the platform (ADR-0006): real OS windows
    /// under a <see cref="Window"/> TopLevel (task 6.0), overlay pseudo-windows elsewhere —
    /// the browser (TW-7.7, DA-7.5, task 6.1). The single branching point of the UI layer.
    /// </summary>
    private IFloatingLayer CreateFloatingLayer(TopLevel topLevel) =>
        topLevel is Window owner && !ForceOverlayFloating
            ? new FloatingWindowLayer(this, owner)
            : new OverlayWindowLayer(this);

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Teardown, not commands (TW-7.5, DA-7.6): the state keeps its floating windows open
        // for the next session.
        _floating?.Teardown();
        _floating = null;
        _autoHide?.DetachAll();
        _autoHide = null;
        // The floating windows unregistered themselves in the teardown; whatever remains —
        // the main TopLevel — leaves with the workspace.
        foreach (var topLevel in _windowZOrder.ToArray())
        {
            UnregisterTopLevel(topLevel);
        }
    }

    /// <summary>
    /// Detaches a host from its current parent — the step before a legitimate reattachment
    /// (TW-9.13: another slot or layer). On a platform with real windows the old
    /// window's layout queue is drained right here, while the control is detached: the
    /// detach/attach transition itself leaves entries for the moving control in the old
    /// root's LayoutManager, and a manager whose queue still names a control that meanwhile
    /// joined another window crashes on it («wrong LayoutManager»; headless probes and the
    /// owner's desktop runs, 2026-07) — a drained detached control is simply dropped from
    /// the queue, in every move direction alike. Draining a clean root costs nothing; under
    /// a non-Window TopLevel (the browser) there is a single root and nothing to protect.
    /// </summary>
    internal static void DetachFromParent(Control control)
    {
        var oldRoot = TopLevel.GetTopLevel(control) as Window;
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
        }

        oldRoot?.UpdateLayout();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty)
        {
            Sync();
        }
        else if (change.Property == RegistryProperty || change.Property == LifecycleProperty)
        {
            // A full reconfiguration: cached hosts and their retained views belong to the
            // previous registry and coordinator.
            Reset();
            Sync();
        }
        else if (change.Property == DefinitionProperty)
        {
            // A definition assigned after the attach applies immediately; before it — at the
            // attach, when every markup property is set. Once applied, a replacement is
            // ignored (see Definition).
            if (!_definitionApplied && ((ILogical)this).IsAttachedToLogicalTree)
            {
                ApplyDefinition();
            }
        }
        else if (change.Property == ShortcutHintProviderProperty
            || change.Property == TabTitleProviderProperty
            || change.Property == WindowTitleSuffixProperty)
        {
            // Tooltips, tab titles and window titles live on leaf chrome: a re-projection
            // rebuilds them (TW-9.13, DA-9.6); the host caches and retained views are
            // untouched, unlike a Registry/Lifecycle swap.
            Sync();
        }
    }

    /// <summary>The incremental projection of TW-9.13: hosts update in place, geometry relays around them.</summary>
    private void Sync()
    {
        if (State is not { } state || Registry is not { } registry)
        {
            Reset();
            return;
        }

        if (_grid is null)
        {
            BuildSkeleton();
        }

        // The tab-host sweep of DA-9.6 runs first: hosts of ids gone from the layout are
        // dropped, released content of unregistered owners is forgotten (TW-9.4) — during the
        // assignment sync, before the coordinator's release in NotifyTransition.
        TabHosts.Sweep(state, registry);

        // Hosts of ids gone from the layout are dropped; the rest update in place.
        List<string>? gone = null;
        foreach (var id in _hosts.Keys)
        {
            if (!state.ToolWindows.Any(w => string.Equals(w.Id, id, StringComparison.Ordinal)))
            {
                (gone ??= []).Add(id);
            }
        }

        if (gone is not null)
        {
            foreach (var id in gone)
            {
                DetachFromParent(_hosts[id]);
                _hosts.Remove(id);
            }
        }

        foreach (var window in state.ToolWindows)
        {
            var hosted = IsHosted(window);
            if (!hosted && !_hosts.ContainsKey(window.Id))
            {
                continue; // never materialized — nothing cached to update
            }

            registry.TryGet(window.Id, out var descriptor);
            GetHost(window.Id).Update(window, descriptor, string.Equals(
                state.ActiveToolWindowId, window.Id, StringComparison.Ordinal), state, registry);
        }

        // The floating layer reconciles first: it adopts every host that moves across
        // windows, so the in-window projections below only ever move hosts within their own
        // root (task 6.0).
        _floating?.Update(state, registry);
        _grid!.Update(state, slot => OpenDocked(state, slot) is { } window ? GetHost(window.Id) : null);
        _dockView!.Update(state, registry);
        _overlay!.Update(state, GetHost);
        _leftStripe!.Update(state, registry, this);
        _rightStripe!.Update(state, registry, this);
        TabHosts.ScheduleMaterialization();
        // A drag in flight survives the re-projection: its targets rebuild over the updated
        // layout, and only a vanished subject cancels the gesture (TW-5.17).
        _drag?.OnProjectionUpdated(state);
    }

    /// <summary>The cached persistent host of one tool window (TW-9.13), created on first need.</summary>
    internal ToolWindowDecorator GetHost(string id)
    {
        if (!_hosts.TryGetValue(id, out var host))
        {
            host = new ToolWindowDecorator(id, this);
            _hosts[id] = host;
        }

        return host;
    }

    private static ToolWindowState? OpenDocked(LayoutState state, ToolWindowSlot slot) =>
        state.ToolWindows.FirstOrDefault(w =>
            w.IsOpen && w.Slot == slot && w.Mode.GetLayer() == ToolWindowLayer.Docked);

    private void BuildSkeleton()
    {
        _leftStripe = new ToolWindowStripe(QuickAccessSide.Left);
        _rightStripe = new ToolWindowStripe(QuickAccessSide.Right);
        _dockView = new DockAreaView(this);
        _grid = new WorkspaceGrid(this, _dockView);
        _overlay = new UndockOverlay();

        // The overlay is the second child, painting above the docked layout (TW-3.3); the
        // drag layer paints above everything (TW-5.17).
        var center = new Panel();
        center.Children.Add(_grid);
        center.Children.Add(_overlay);
        _dragLayer = new DragLayer();
        _pseudoLayer = new Canvas { Name = "PART_PseudoWindowLayer" };

        DockPanel.SetDock(_leftStripe, Dock.Left);
        DockPanel.SetDock(_rightStripe, Dock.Right);
        var workspaceRow = new DockPanel();
        workspaceRow.Children.Add(_leftStripe);
        workspaceRow.Children.Add(_rightStripe);
        workspaceRow.Children.Add(center);

        // The pseudo-window canvas spans the whole workspace, stripes included — the
        // workspace is the «screen» of the overlay platform (TW-7.7); empty on the desktop.
        // The drag layer paints above it — its ghost and markers live in workspace
        // coordinates too (TW-5.17).
        var root = new Panel();
        root.Children.Add(workspaceRow);
        root.Children.Add(_pseudoLayer);
        root.Children.Add(_dragLayer);
        Child = root;

        _drag ??= new DragController(this); // the workspace-level handlers attach once
        _drag.Layer = _dragLayer;
    }

    private void Reset()
    {
        // Floating windows close first (no commands): their hosted content returns to the
        // caches below; the layer itself survives for the next Sync while still attached.
        _floating?.Teardown();
        if (_floating is not null && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _floating = CreateFloatingLayer(topLevel);
        }

        foreach (var host in _hosts.Values)
        {
            DetachFromParent(host);
        }

        _hosts.Clear();
        _tabHosts?.Clear();
        _tabHosts = null; // the tab-host cache and retained tab views die with the projection
        _drag?.Reset(); // a gesture in flight ends with no trace (TW-5.17)
        _dragLayer = null;
        _pseudoLayer = null;
        _leftStripe = null;
        _rightStripe = null;
        _grid = null;
        _overlay = null;
        _dockView = null;
        Child = null;
    }
}
