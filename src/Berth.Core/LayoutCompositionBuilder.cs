namespace Berth;

/// <summary>
/// Fluent assembly of an application's initial composition (task 7.1) — a declarative facade
/// over the existing bricks, introducing no behaviour of its own: registrations run through
/// <see cref="ContentLifecycle.Register"/>/<see cref="ContentLifecycle.RegisterDockContent"/>
/// (atomic reconciliation, eager creation, body seeding — spec TW-9.2, TW-9.5, TW-10.3), the
/// default arrangement follows the rule of <see cref="LayoutApply.ResetToDefaults"/>
/// (<see cref="ToolWindowBuilder.Order"/> is honoured like <see cref="ToolWindowDescriptor.DefaultOrder"/>),
/// and the initial openness is expressed by the ordinary commands — one
/// <see cref="ContentLifecycle.NotifyTransition"/> per command (ADR-0004; openness is not a
/// descriptor field, spec E15). <see cref="Build"/> runs in two phases: every registration
/// first, then the open commands in call order — so calls may be chained in any order, and
/// errors (an unknown id, an unclaimed panel tab) surface at <see cref="Build"/>. The builder
/// is single-use: the registry and the coordinator it produces are mutable.
/// </summary>
public sealed class LayoutCompositionBuilder
{
    private enum InitialCommand
    {
        OpenToolWindow,
        OpenDocument,
        OpenPanelTab,
    }

    private readonly List<ToolWindowDescriptor> _toolWindows = [];
    private readonly List<ITabContentFactory> _dockContent = [];
    private readonly List<(InitialCommand Kind, string Id)> _commands = [];
    private bool _built;

    /// <summary>Registers a tool window from a ready descriptor (spec TW-9.1).</summary>
    /// <param name="descriptor">The registration descriptor.</param>
    /// <exception cref="ArgumentException">A descriptor with the same id was already added.</exception>
    public LayoutCompositionBuilder AddToolWindow(ToolWindowDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_toolWindows.Any(d => string.Equals(d.Id, descriptor.Id, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Tool window '{descriptor.Id}' was already added to the composition.", nameof(descriptor));
        }

        _toolWindows.Add(descriptor);
        return this;
    }

    /// <summary>Registers a tool window through a configurator — the fluent counterpart of the descriptor.</summary>
    /// <param name="id">Stable identifier; must be non-empty.</param>
    /// <param name="title">Human-readable title.</param>
    /// <param name="configure">Configuration of the placement, policies and content.</param>
    /// <exception cref="ArgumentException">The id or title is empty, or the id was already added.</exception>
    public LayoutCompositionBuilder AddToolWindow(string id, string title, Action<ToolWindowBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ToolWindowBuilder(id, title);
        configure(builder);
        return AddToolWindow(builder.BuildDescriptor());
    }

    /// <summary>Registers dock-area content: the factory's claimed tabs are documents (spec TW-9.11).</summary>
    /// <param name="factory">The dock content factory.</param>
    public LayoutCompositionBuilder AddDockContent(ITabContentFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _dockContent.Add(factory);
        return this;
    }

    /// <summary>Registers dock-area content by delegates — a claim predicate plus creation (spec TW-9.11, DA-9.3).</summary>
    /// <param name="ownsTab">The ownership claim; must be pure and stable (spec TW-9.11).</param>
    /// <param name="createContent">Creates the content of a claimed tab; null refuses the tab (spec DA-9.3).</param>
    /// <param name="releaseContent">Releases created content; null when nothing needs releasing.</param>
    public LayoutCompositionBuilder AddDockContent(
        Func<string, bool> ownsTab,
        Func<string, object?> createContent,
        Action<string, object>? releaseContent = null)
    {
        ArgumentNullException.ThrowIfNull(ownsTab);
        ArgumentNullException.ThrowIfNull(createContent);
        return AddDockContent(new DelegateTabContentFactory(ownsTab, createContent, releaseContent));
    }

    /// <summary>
    /// Opens a tool window in the initial state (spec TW-5.1, with activation). Openness is a
    /// command, not a descriptor field (spec E15): the built <see cref="LayoutComposition.State"/>
    /// carries it, and later applies of that state restore it.
    /// </summary>
    /// <param name="toolWindowId">Id of a tool window added to this composition.</param>
    public LayoutCompositionBuilder Open(string toolWindowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolWindowId);
        _commands.Add((InitialCommand.OpenToolWindow, toolWindowId));
        return this;
    }

    /// <summary>Opens a document in the initial state (spec DA-5.1).</summary>
    /// <param name="tabId">Id of the document tab.</param>
    public LayoutCompositionBuilder OpenDocument(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        _commands.Add((InitialCommand.OpenDocument, tabId));
        return this;
    }

    /// <summary>Opens a panel tab in the tree of its claimed owner in the initial state (spec TW-9.12).</summary>
    /// <param name="tabId">Id of the panel tab; its owner must be claimed by a tool window of this composition.</param>
    public LayoutCompositionBuilder OpenPanelTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        _commands.Add((InitialCommand.OpenPanelTab, tabId));
        return this;
    }

    /// <summary>
    /// Builds the composition: registers everything through a fresh
    /// <see cref="ContentLifecycle"/>, derives the default arrangement by the
    /// <see cref="LayoutApply.ResetToDefaults"/> rule, then applies the open commands in call
    /// order — each one core command with one transition report (ADR-0004). The result always
    /// satisfies the invariants of both specs and passes Apply without a single fix
    /// (TW-10.4) — guarded by tests.
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder was already built.</exception>
    /// <exception cref="ArgumentException">An open command references an id the composition cannot resolve.</exception>
    public LayoutComposition Build()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "The composition was already built; a builder is single-use — the registry and coordinator it produces are mutable.");
        }

        _built = true;
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty;
        foreach (var descriptor in _toolWindows)
        {
            state = lifecycle.Register(state, descriptor);
        }

        foreach (var factory in _dockContent)
        {
            state = lifecycle.RegisterDockContent(state, factory);
        }

        // The registration chain ordered slots by call order (the live-session rule of
        // TW-10.3); the canonical default arrangement honours DefaultOrder instead — rebuild
        // the placement by the single defaults rule, so the built state and a later
        // ResetToDefaults agree (spec TW-5.14).
        state = LayoutApply.ResetToDefaults(registry);

        foreach (var (kind, id) in _commands)
        {
            var next = kind switch
            {
                InitialCommand.OpenToolWindow => state.Open(id),
                InitialCommand.OpenDocument => state.OpenDocument(id, registry),
                _ => state.OpenPanelTab(id, registry),
            };
            lifecycle.NotifyTransition(state, next);
            state = next;
        }

        return new LayoutComposition(registry, lifecycle, state);
    }
}

/// <summary>
/// Configurator of one tool window inside
/// <see cref="LayoutCompositionBuilder.AddToolWindow(string, string, Action{ToolWindowBuilder})"/>:
/// a fluent view of <see cref="ToolWindowDescriptor"/>, with unnamed defaults matching the
/// descriptor's (DockPinned, OnFirstOpen, KeepWhileRegistered, pair ratio 0.5). Every setter
/// overwrites the previous value, like property assignment.
/// </summary>
public sealed class ToolWindowBuilder
{
    private readonly string _id;
    private readonly string _title;
    private ToolWindowSlot _slot;
    private int? _order;
    private ToolWindowMode _mode = ToolWindowMode.DockPinned;
    private double _pairRatio = LayoutDefaults.PairRatio;
    private string? _iconKey;
    private ContentCreationPolicy _creationPolicy;
    private ContentRetentionPolicy _retentionPolicy;
    private IToolWindowContentFactory? _contentFactory;
    private ITabContentFactory? _tabFactory;

    internal ToolWindowBuilder(string id, string title)
    {
        _id = id;
        _title = title;
    }

    /// <summary>Default placement slot (spec TW-1.1); unset — Left.Primary.</summary>
    public ToolWindowBuilder Slot(ToolWindowSide side, ToolWindowGroup group)
    {
        _slot = new ToolWindowSlot(side, group);
        return this;
    }

    /// <summary>Default position within the slot — <see cref="ToolWindowDescriptor.DefaultOrder"/>; unset — after the windows added earlier (spec TW-10.3).</summary>
    public ToolWindowBuilder Order(int order)
    {
        _order = order;
        return this;
    }

    /// <summary>Default presentation mode (spec TW-3.2).</summary>
    public ToolWindowBuilder Mode(ToolWindowMode mode)
    {
        _mode = mode;
        return this;
    }

    /// <summary>Default share preference within a side pair (spec TW-2.5).</summary>
    public ToolWindowBuilder PairRatio(double pairRatio)
    {
        _pairRatio = pairRatio;
        return this;
    }

    /// <summary>Icon key resolved by the materialization layer or the application (ADR-0003).</summary>
    public ToolWindowBuilder Icon(string iconKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconKey);
        _iconKey = iconKey;
        return this;
    }

    /// <summary>Body content is created at registration instead of on first materialization (spec TW-9.2).</summary>
    public ToolWindowBuilder Eager()
    {
        _creationPolicy = ContentCreationPolicy.Eager;
        return this;
    }

    /// <summary>Body content is released on every transition out of openness and recreated on the next one (spec TW-9.2).</summary>
    public ToolWindowBuilder DisposeOnClose()
    {
        _retentionPolicy = ContentRetentionPolicy.DisposeOnClose;
        return this;
    }

    /// <summary>Factory of the body content (spec TW-9.1, TW-9.5).</summary>
    public ToolWindowBuilder Content(IToolWindowContentFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _contentFactory = factory;
        return this;
    }

    /// <summary>Body content by delegates: creation plus optional release (spec TW-9.1, TW-9.2).</summary>
    /// <param name="createContent">Creates the body content.</param>
    /// <param name="releaseContent">Releases created content; null when nothing needs releasing.</param>
    public ToolWindowBuilder Content(Func<string, object> createContent, Action<string, object>? releaseContent = null)
    {
        ArgumentNullException.ThrowIfNull(createContent);
        return Content(new DelegateToolWindowContentFactory(createContent, releaseContent));
    }

    /// <summary>Tab content factory claiming this window's tabs (spec TW-9.11).</summary>
    public ToolWindowBuilder Tabs(ITabContentFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _tabFactory = factory;
        return this;
    }

    /// <summary>Tab claims by delegates: an ownership predicate plus creation (spec TW-9.11, DA-9.3).</summary>
    /// <param name="ownsTab">The ownership claim; must be pure and stable (spec TW-9.11).</param>
    /// <param name="createContent">Creates the content of a claimed tab; null refuses the tab (spec DA-9.3).</param>
    /// <param name="releaseContent">Releases created content; null when nothing needs releasing.</param>
    public ToolWindowBuilder Tabs(
        Func<string, bool> ownsTab,
        Func<string, object?> createContent,
        Action<string, object>? releaseContent = null)
    {
        ArgumentNullException.ThrowIfNull(ownsTab);
        ArgumentNullException.ThrowIfNull(createContent);
        return Tabs(new DelegateTabContentFactory(ownsTab, createContent, releaseContent));
    }

    internal ToolWindowDescriptor BuildDescriptor() => new(_id, _title, _slot)
    {
        DefaultOrder = _order,
        DefaultMode = _mode,
        DefaultPairRatio = _pairRatio,
        IconKey = _iconKey,
        CreationPolicy = _creationPolicy,
        RetentionPolicy = _retentionPolicy,
        ContentFactory = _contentFactory,
        TabFactory = _tabFactory,
    };
}

/// <summary>Delegate adapter of <see cref="IToolWindowContentFactory"/> for the fluent builder.</summary>
internal sealed class DelegateToolWindowContentFactory(
    Func<string, object> create, Action<string, object>? release) : IToolWindowContentFactory
{
    public object CreateContent(string toolWindowId) => create(toolWindowId);

    public void ReleaseContent(string toolWindowId, object content) => release?.Invoke(toolWindowId, content);
}

/// <summary>Delegate adapter of <see cref="ITabContentFactory"/> for the fluent builder.</summary>
internal sealed class DelegateTabContentFactory(
    Func<string, bool> owns, Func<string, object?> create, Action<string, object>? release) : ITabContentFactory
{
    public bool OwnsTab(string tabId) => owns(tabId);

    public object? CreateContent(string tabId) => create(tabId);

    public void ReleaseContent(string tabId, object content) => release?.Invoke(tabId, content);
}
