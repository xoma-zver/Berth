using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Berth.Controls;

/// <summary>
/// Root control materializing a <see cref="LayoutState"/> (spec TW-2.1): the stripes at the
/// left and right edges; between them the side panes and the dock area above the bottom pane,
/// which spans the full width between the stripes; open Undock windows overlay the workspace
/// (TW-3.3). Float and Window modes are not materialized until the floating-window phase —
/// only their stripe icons show. The control is a pure projection of the state (ADR-0002):
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
/// while the workspace is attached.
///
/// Materialization is incremental (spec TW-9.13): the visual skeleton is built once, and each
/// state change is projected as a diff. Tool window hosts (<see cref="ToolWindowDecorator"/>)
/// are cached per id, updated in place and reattached only when the window actually moves to
/// another slot or layer, so keyboard focus and view-state are never lost to materialization
/// itself; sides and pairs collapse and expand around the remaining hosts, and a closed
/// KeepWhileRegistered host retains its built view until reopening or unregistration. Leaf
/// chrome — stripe buttons, menus, splitters — is rebuilt per update. With a
/// <see cref="Lifecycle"/> attached, decorator bodies materialize through the factory bridge
/// and every gesture command reports its transition to the coordinator (TW-9.3). Registry
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

    private readonly Dictionary<string, ToolWindowDecorator> _hosts = new(StringComparer.Ordinal);
    private ToolWindowStripe? _leftStripe;
    private ToolWindowStripe? _rightStripe;
    private WorkspaceGrid? _grid;
    private UndockOverlay? _overlay;
    private AutoHideController? _autoHide;

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
    /// Optional content coordinator. When set, decorator bodies materialize through the
    /// factory bridge (spec TW-9.3, TW-9.5): a <see cref="Control"/> content object is hosted
    /// directly, anything else gets its view built once by the application's data templates
    /// and hosted for the content's lifetime (TW-9.13). Every gesture command reports its transition to
    /// <see cref="ContentLifecycle.NotifyTransition"/> — exactly one call per command
    /// (ADR-0004); transitions the application performs itself — direct <see cref="State"/>
    /// assignments, Apply, ResetToDefaults — remain the application's to report. Null keeps
    /// the static skeleton: bodies stay placeholders.
    /// </summary>
    public ContentLifecycle? Lifecycle
    {
        get => GetValue(LifecycleProperty);
        set => SetValue(LifecycleProperty, value);
    }

    /// <summary>
    /// Projects the current <see cref="State"/> and <see cref="Registry"/> again. Required
    /// after operations that mutate the registry in place — the live registration lifecycle
    /// (<see cref="ContentLifecycle.Register"/>, RegisterDockContent, Unregister): they may
    /// return a state value-equal to the current one while titles, icons and claims changed,
    /// and the property system deduplicates equal assignments. Core layout commands need no
    /// explicit refresh beyond assigning their result to <see cref="State"/>.
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
            var result = command(state);
            // Assign first: the sync detaches released content from the visual tree before
            // the coordinator hands it back to its factory.
            State = result;
            Lifecycle?.NotifyTransition(state, result);
            // The focus transfer of activation (TW-6.6) runs last: a nested command raised
            // by the focus change — the auto-hide close of the focus loser (TW-6.1) — then
            // reports its own transition after this one, in gesture order. Recursion is
            // finite: the nested commands never activate another window.
            if (result.ActiveToolWindowId is { } activated
                && !string.Equals(activated, state.ActiveToolWindowId, StringComparison.Ordinal))
            {
                FocusActivated(activated);
            }
        }
    }

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
            var hosted = window.IsOpen && window.Mode.GetLayer() != ToolWindowLayer.Floating;
            if (!hosted && !_hosts.ContainsKey(window.Id))
            {
                continue; // never materialized — nothing cached to update
            }

            registry.TryGet(window.Id, out var descriptor);
            GetHost(window.Id).Update(window, descriptor, string.Equals(
                state.ActiveToolWindowId, window.Id, StringComparison.Ordinal));
        }

        _grid!.Update(state, slot => OpenDocked(state, slot) is { } window ? GetHost(window.Id) : null);
        _overlay!.Update(state, GetHost);
        _leftStripe!.Update(state, registry, this);
        _rightStripe!.Update(state, registry, this);
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
        _grid = new WorkspaceGrid(this);
        _overlay = new UndockOverlay();

        // The overlay is the second child, painting above the docked layout (TW-3.3).
        var center = new Panel();
        center.Children.Add(_grid);
        center.Children.Add(_overlay);

        DockPanel.SetDock(_leftStripe, Dock.Left);
        DockPanel.SetDock(_rightStripe, Dock.Right);
        var root = new DockPanel();
        root.Children.Add(_leftStripe);
        root.Children.Add(_rightStripe);
        root.Children.Add(center);
        Child = root;
    }

    private void Reset()
    {
        foreach (var host in _hosts.Values)
        {
            DetachFromParent(host);
        }

        _hosts.Clear();
        _leftStripe = null;
        _rightStripe = null;
        _grid = null;
        _overlay = null;
        Child = null;
    }
}
