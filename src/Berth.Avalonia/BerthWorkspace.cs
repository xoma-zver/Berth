using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// Root control materializing a <see cref="LayoutState"/> (spec TW-2.1): the stripes at the
/// left and right edges; between them the side panes and the dock area above the bottom pane,
/// which spans the full width between the stripes; open Undock windows overlay the workspace
/// (TW-3.3). The dock area and the content trees of open tool windows materialize their tab
/// trees by one shared projection (TW-2.1, TW-9.5, DA-9.6): tab groups with strips — a panel
/// root group's strip lives in the decorator header row, hidden for the degenerate solitary
/// body (DA-8.4) — splits with splitters, the active tab's content per group; tab hosts come
/// from a single workspace-wide cache keyed by tab id, so a move between a panel and the dock
/// area reattaches the same host with its built view. Tab clicks, tab menus, split drags and
/// tab drag-and-drop reduce to the dock commands (DA-5.2…DA-5.9, DA-9.7, ADR-0004), focus
/// gains in tab content reduce to ActivateTab (DA-6.4), and Esc inside a tool window returns
/// focus to the current tab (TW-6.3). Document windows, like Float and Window tool window modes, are not materialized
/// until the floating-window phase — their tabs keep cached views while away. The control is a pure projection of the state (ADR-0002):
/// fractions become pixels here and render-time minimums clamp without touching the state
/// (TW-2.8). Input reduces to core commands (ADR-0004): a stripe icon click toggles openness
/// (TW-5.4), the decorator buttons close the window and open its menu (TW-5.3, TW-5.16), the
/// menus change modes and placement (TW-5.16), the «⋯» flyout restores hidden windows
/// (TW-8.3), and splitter drags are pure visuals until the release commits one resize command
/// (TW-5.9, TW-2.7 R2). Every command assigns its result back to <see cref="State"/> —
/// observe user-driven changes with <c>GetObservable(StateProperty)</c>. Activation transfers
/// keyboard focus into the activated window's content and focus gains inside a window
/// activate it (TW-6.6); DockUnpinned/Undock windows auto-hide on focus loss and on outside
/// clicks (TW-6.1, TW-6.2) — wired by an <see cref="AutoHideController"/> on the TopLevel
/// while the workspace is attached. The application binds its keymap to
/// <see cref="ActivateToolWindow"/> — the tri-state activation shortcut of TW-5.5 — and may
/// supply <see cref="ShortcutHintProvider"/> to show the shortcuts in stripe icon tooltips
/// (TW-6.4).
///
/// Materialization is incremental (spec TW-9.13): the visual skeleton is built once, and each
/// state change is projected as a diff. Tool window hosts (<see cref="ToolWindowDecorator"/>)
/// are cached per id, updated in place and reattached only when the window actually moves to
/// another slot or layer, so keyboard focus and view-state are never lost to materialization
/// itself; sides and pairs collapse and expand around the remaining hosts, and a closed
/// KeepWhileRegistered host retains its built view until reopening or unregistration. Leaf
/// chrome — stripe buttons, tab headers, menus, splitters — is rebuilt per update. With a
/// <see cref="Lifecycle"/> attached, tab content materializes through the coordinator's pull
/// pass and every gesture command reports its transition to it (TW-9.3). Registry
/// mutations are invisible to the property system — and the live registration operations of
/// <see cref="ContentLifecycle"/> may return a value-equal state, which assignment
/// deduplicates — so call <see cref="Refresh"/> after them. Replacing <see cref="Registry"/>
/// or <see cref="Lifecycle"/> is a full reconfiguration: the skeleton and the host cache are
/// rebuilt from scratch and retained views are dropped.
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

    /// <summary>Defines the <see cref="ShortcutHintProvider"/> property.</summary>
    public static readonly StyledProperty<Func<string, string?>?> ShortcutHintProviderProperty =
        AvaloniaProperty.Register<BerthWorkspace, Func<string, string?>?>(nameof(ShortcutHintProvider));

    /// <summary>Defines the <see cref="TabTitleProvider"/> property.</summary>
    public static readonly StyledProperty<Func<string, string?>?> TabTitleProviderProperty =
        AvaloniaProperty.Register<BerthWorkspace, Func<string, string?>?>(nameof(TabTitleProvider));

    private readonly Dictionary<string, ToolWindowDecorator> _hosts = new(StringComparer.Ordinal);
    private TabHostCache? _tabHosts;
    private ToolWindowStripe? _leftStripe;
    private ToolWindowStripe? _rightStripe;
    private WorkspaceGrid? _grid;
    private UndockOverlay? _overlay;
    private DockAreaView? _dockView;
    private DragLayer? _dragLayer;
    private DragController? _drag;
    private AutoHideController? _autoHide;

    /// <summary>
    /// The single tab-host cache of the workspace (spec DA-9.6): shared by the dock area and
    /// the panel trees, so a move between hosts reattaches the same host with its built view.
    /// </summary>
    internal TabHostCache TabHosts => _tabHosts ??= new TabHostCache(this);

    /// <summary>The drag gesture controller (spec TW-5.17); null until the skeleton is built.</summary>
    internal DragController? Drag => _drag;

    /// <summary>The layout to materialize; null renders nothing.</summary>
    public LayoutState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Registrations supplying titles and icons (spec TW-9.1); null renders nothing.</summary>
    public ToolWindowRegistry? Registry
    {
        get => GetValue(RegistryProperty);
        set => SetValue(RegistryProperty, value);
    }

    /// <summary>
    /// Optional content coordinator. When set, tab content — documents, panel tabs and panel
    /// bodies via the factory bridge of TW-9.5 — materializes lazily through
    /// <see cref="ContentLifecycle.MaterializeTab"/> in a pull pass outside the projection
    /// (spec TW-9.3, DA-9.3): a <see cref="Control"/> content object is hosted directly,
    /// anything else gets its view built once by the application's data templates and hosted
    /// for the content's lifetime (TW-9.13, DA-9.6). Every gesture command reports its
    /// transition to <see cref="ContentLifecycle.NotifyTransition"/> — exactly one call per
    /// command (ADR-0004); transitions the application performs itself — direct
    /// <see cref="State"/> assignments, Apply, ResetToDefaults — remain the application's to
    /// report. Null keeps the static skeleton: tabs stay placeholders.
    /// </summary>
    public ContentLifecycle? Lifecycle
    {
        get => GetValue(LifecycleProperty);
        set => SetValue(LifecycleProperty, value);
    }

    /// <summary>
    /// Optional application-supplied shortcut hints (spec TW-5.5, TW-6.4): maps a tool window
    /// id to the display string of its activation shortcut, which extends the stripe icon
    /// tooltip beyond the title. The shortcuts themselves are the application's keymap — bind
    /// them to <see cref="ActivateToolWindow"/>; the provider only supplies what to display.
    /// Null — the provider or its result for an id — leaves the tooltip as the bare title.
    /// </summary>
    public Func<string, string?>? ShortcutHintProvider
    {
        get => GetValue(ShortcutHintProviderProperty);
        set => SetValue(ShortcutHintProviderProperty, value);
    }

    /// <summary>
    /// Optional application-supplied tab titles (spec DA-9.6): maps a tab id to the display
    /// string of its header and placeholder. The layout stores identifiers only (ADR-0003),
    /// and a title must exist before content materializes and for sleeping tabs (DA-9.4), so
    /// titles come from the application, not from the content. Null — the provider or its
    /// result for an id — falls back to the id itself.
    /// </summary>
    public Func<string, string?>? TabTitleProvider
    {
        get => GetValue(TabTitleProviderProperty);
        set => SetValue(TabTitleProviderProperty, value);
    }

    /// <summary>
    /// The activation shortcut of one tool window (spec TW-5.5) — the public entry the
    /// application binds its keymap to: a closed window opens and activates, an open inactive
    /// one activates, an open active one closes. «Active» is decided by keyboard focus lying
    /// logically within the window or its stripe icon — the equivalent of the reference, whose
    /// active id derives from focus; the stored <see cref="LayoutState.ActiveToolWindowId"/>
    /// lags behind focus (TW-6.6: with focus in another window it still names the previous
    /// active) and a check against it would close a window without focus. Runs through the
    /// command channel: activation
    /// transfers focus into the content (TW-6.6) and the transition is reported to
    /// <see cref="Lifecycle"/> once (ADR-0004). A no-op without a state; an id absent from
    /// the layout is a caller error, as in the core operations.
    /// </summary>
    /// <param name="id">Id of a tool window present in the layout.</param>
    /// <exception cref="ArgumentException">The id is empty, or no such tool window exists in the layout.</exception>
    public void ActivateToolWindow(string id)
    {
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
    /// them up (DA-9.4). Core layout commands need no explicit refresh beyond assigning their
    /// result to <see cref="State"/>.
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
    /// The dock side of the focus contract (spec DA-9.6, DA-E21, TW-6.3): when the command
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

            _tabHosts.TryFocusTab(after.DockArea.CurrentTabId);
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
            _tabHosts.TryFocusTab(after.DockArea.CurrentTabId);
        }
    }

    /// <summary>
    /// Focuses the current tab of the effective active host (spec TW-6.3, DA-E21) — the main
    /// window until document windows materialize (phase 6): a stored ActiveDockHost pointing
    /// at a document window of a restored layout degrades in presentation only (DA-6.4).
    /// False without a target — the empty main window (TW-6.3: Esc without a target is a
    /// no-op).
    /// </summary>
    internal bool FocusCurrentDockTab() => _tabHosts?.TryFocusTab(State?.DockArea.CurrentTabId) == true;

    /// <summary>The focus transfer of a tab gesture (DA-6.4); a no-op when the tab has no attached host.</summary>
    internal void FocusTab(string id) => _tabHosts?.TryFocusTab(id);

    /// <summary>
    /// Whether the command factually reattached the window's host: open before and after with
    /// the slot or the layer changed — the reattachment whitelist of spec TW-9.13
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
    /// overlay mode (spec TW-3.2); floating modes are not materialized until phase 6. The
    /// shared gate of the decorator projection, its tree materialization and the pull pass.
    /// </summary>
    internal static bool IsHosted(ToolWindowState window) =>
        window.IsOpen && window.Mode.GetLayer() != ToolWindowLayer.Floating;

    /// <summary>
    /// The focus transfer of spec TW-6.6: keyboard focus moves into the activated window's
    /// content — unless it is already inside (activation never re-arranges focus within the
    /// window) or the window has no attached host (a floating-mode record before phase 6).
    /// Only the command funnel transfers focus: direct <see cref="State"/> assignments by the
    /// application do not pass here (TW-6.6).
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

        host.FocusContent();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _autoHide = new AutoHideController(this, topLevel);
        }
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _autoHide?.Detach();
        _autoHide = null;
    }

    /// <summary>
    /// Detaches a host from its current parent — the step before a legitimate reattachment
    /// (spec TW-9.13: another slot or layer).
    /// </summary>
    internal static void DetachFromParent(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
        }
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
        else if (change.Property == ShortcutHintProviderProperty || change.Property == TabTitleProviderProperty)
        {
            // Tooltips and tab titles live on leaf chrome: a re-projection rebuilds them
            // (TW-9.13, DA-9.6); the host caches and retained views are untouched, unlike a
            // Registry/Lifecycle swap.
            Sync();
        }
    }

    /// <summary>The incremental projection of spec TW-9.13: hosts update in place, geometry relays around them.</summary>
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

    private ToolWindowDecorator GetHost(string id)
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

        DockPanel.SetDock(_leftStripe, Dock.Left);
        DockPanel.SetDock(_rightStripe, Dock.Right);
        var workspaceRow = new DockPanel();
        workspaceRow.Children.Add(_leftStripe);
        workspaceRow.Children.Add(_rightStripe);
        workspaceRow.Children.Add(center);

        // The drag layer spans the whole workspace, stripes included — its ghost and markers
        // live in workspace coordinates (TW-5.17).
        var root = new Panel();
        root.Children.Add(workspaceRow);
        root.Children.Add(_dragLayer);
        Child = root;

        _drag ??= new DragController(this); // the workspace-level handlers attach once
        _drag.Layer = _dragLayer;
    }

    private void Reset()
    {
        foreach (var host in _hosts.Values)
        {
            DetachFromParent(host);
        }

        _hosts.Clear();
        _tabHosts?.Clear();
        _tabHosts = null; // the tab-host cache and retained tab views die with the projection
        _drag?.Reset(); // a gesture in flight ends with no trace (TW-5.17)
        _dragLayer = null;
        _leftStripe = null;
        _rightStripe = null;
        _grid = null;
        _overlay = null;
        _dockView = null;
        Child = null;
    }
}
