using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Berth.Controls;

/// <summary>
/// Materialization of the floating layer on a platform with real windows (spec TW-7.1…TW-7.5,
/// DA-7.1…DA-7.3, DA-7.6): open Float/Window tool windows become OS windows hosting the same
/// cached <see cref="ToolWindowDecorator"/> — a move between the docked layout and a floating
/// window is the whitelisted layer-change reattachment of TW-9.13 — and every document window
/// of the state becomes an independent OS window projecting its tab tree by the shared
/// projection over the workspace-wide host cache (DA-9.6). The layer is a pure projection of
/// the state (ADR-0002): reconciliation is keyed by tool window id for panels and by tab-set
/// overlap for document windows, which have no identity of their own (DA-1.3). Window gestures
/// reduce to core commands (ADR-0004): the system close button issues Close (TW-7.3) or one
/// CloseTab per tab (DA-7.3), moves and resizes commit SetFloatingBounds and
/// SetDocumentWindowBounds (TW-5.9, DA-5.8) with equal-value guards breaking the feedback
/// loop; the user gesture cancels the platform close and lets the resulting state change close
/// the window, so a state-driven close never doubles as a command. Closing the main window —
/// and detaching the workspace — tears every floating window down without commands: the state
/// keeps the windows open for the next session (TW-7.5, DA-7.6). Each floating window
/// registers with the workspace's <see cref="AutoHideController"/>, so focus and click wiring
/// spans every window of the workspace (TW-6.1, TW-6.2, DA-6.4).
/// </summary>
internal sealed class FloatingWindowLayer : IFloatingLayer
{
    private readonly BerthWorkspace _workspace;
    private readonly Window _owner;
    private readonly Dictionary<string, PanelWindow> _panels = new(StringComparer.Ordinal);
    private readonly List<DocumentWindow> _documents = [];
    private bool _torndown;

    public FloatingWindowLayer(BerthWorkspace workspace, Window owner)
    {
        _workspace = workspace;
        _owner = owner;
        // Closing the main window must tear down the independent windows too — Window-mode
        // panels and document windows, which no platform cascade reaches (TW-7.5, DA-7.6).
        // The owned-Float cascade is recognized by CloseReason instead: it raises Closing on
        // the owned windows before this handler runs (headless probe, 2026-07).
        _owner.Closing += OnOwnerClosing;
    }

    /// <inheritdoc/>
    public bool IsWindowed => true;

    /// <summary>The reconciliation pass, run from the workspace projection (spec TW-9.13, DA-9.6).</summary>
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
                panel = new PanelWindow(window.Id, independent)
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

            panel.Title = registry.TryGet(window.Id, out var descriptor) ? descriptor.Title : window.Id;
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
            view.Title = TabHostCache.TitleOf(_workspace, windows[i].CurrentTabId);
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

    private void WireCommon(FloatingWindowBase window)
    {
        window.PositionChanged += (_, _) => ScheduleCommit(window);
        window.SizeChanged += (_, _) => ScheduleCommit(window);
    }

    /// <summary>
    /// Defers the bounds commit onto the dispatcher, coalescing event storms into one command
    /// (throttling is allowed, TW-5.9, DA-5.8). Never commits synchronously from the window
    /// event: on a native platform Show() runs the new window's initial layout pass inside
    /// the projection, and the OS adjusts bounds during it — a synchronous command there
    /// re-enters Sync inside a running layout pass and crashes the LayoutManager (owner's
    /// desktop run, 2026-07). The deferred commit re-reads the settled bounds and the live
    /// state, so the guards filter everything the projection itself caused.
    /// </summary>
    private void ScheduleCommit(FloatingWindowBase window)
    {
        if (_torndown || window.SuppressCommands || window.CommitScheduled)
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

        /// <summary>Last bounds applied or committed; an equal state value is not re-applied.</summary>
        public FloatingBounds? AppliedBounds { get; set; }

        /// <summary>Returns the hosted content to the workspace caches before the window closes.</summary>
        public abstract void ReleaseContent();
    }

    /// <summary>OS window of one Float/Window tool window (TW-7.1, TW-7.2), hosting its cached decorator.</summary>
    private sealed class PanelWindow : FloatingWindowBase
    {
        public PanelWindow(string toolWindowId, bool independent)
        {
            ToolWindowId = toolWindowId;
            IsIndependent = independent;
            Content = HostSlot;
        }

        public string ToolWindowId { get; }

        /// <summary>True for the Window mode (independent top-level), false for Float (owned).</summary>
        public bool IsIndependent { get; }

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
    }

    /// <summary>OS window of one document window (DA-7.1), projecting its tab tree (DA-9.6).</summary>
    private sealed class DocumentWindow : FloatingWindowBase
    {
        public DocumentWindow(TabTreeContext context)
        {
            Context = context;
            Content = TreeSlot;
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
