using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// The non-reentrant projection pass of TW-9.14: a core command executed in the middle of a
/// projection — the real sources are focus reactions to host reattachment (DA-6.4, TW-6.1)
/// and platform window events (TW-7.x) — applies to the state immediately but does not start
/// a nested pass; the projection re-runs once the current pass completes, over the live
/// state. Without the guard the nested pass sees the document-window caches mid-mutation and
/// the DA-1.3 overlap matching creates a duplicate window for one state entry — the ghost
/// with a tab strip and no content. The convergence cap fails an oscillating command pair
/// loudly, and a registry swap requested mid-pass defers its cache teardown to the funnel.
/// The seam is deterministic here: tab content acting from its visual-tree attachment, which
/// the projection itself performs.
/// </summary>
public class ProjectionReentrancyTests
{
    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static TabGroupNode Group(string? active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    /// <summary>Tab content that acts whenever the projection attaches it, recording whether a pass was running.</summary>
    private sealed class ActOnAttach : TextBox
    {
        public BerthWorkspace? Workspace { get; set; }

        public Action<BerthWorkspace>? OnAttach { get; set; }

        public int FiredInsideProjection { get; private set; }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (Workspace is { } workspace && OnAttach is { } act)
            {
                if (workspace.IsProjecting)
                {
                    FiredInsideProjection++;
                }

                act(workspace);
            }
        }
    }

    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle, ActOnAttach Content) DockSetup()
    {
        var registry = new ToolWindowRegistry();
        var content = new ActOnAttach();
        registry.RegisterDockContent(new CountingTabFactory("d", _ => content));
        return (registry, new ContentLifecycle(registry), content);
    }

    private static LayoutState MainWindowTab() => LayoutState.Empty with
    {
        DockArea = new DockAreaState { Root = Group("d1", "d1"), CurrentTabId = "d1" },
    };

    private static Func<LayoutState, LayoutState> SetBounds(FloatingBounds bounds) =>
        s => DockTrees.LayoutContainsTab(s, "d1") ? s.SetDocumentWindowBounds("d1", bounds) : s;

    [AvaloniaFact]
    public void TW_9_14_command_during_projection_does_not_duplicate_document_windows()
    {
        var (registry, lifecycle, content) = DockSetup();
        var main = Show(MainWindowTab(), registry, lifecycle: lifecycle);
        var workspace = Workspace(main);
        var nested = new FloatingBounds(220, 230, 400, 300);
        content.Workspace = workspace;
        content.OnAttach = ws => ws.Execute(SetBounds(nested));

        workspace.Execute(s => s.MoveTabToNewWindow("d1", new FloatingBounds(50, 60, 400, 300)));
        Dispatcher.UIThread.RunJobs();

        Assert.True(content.FiredInsideProjection > 0); // the command ran inside the pass
        Assert.Single(St(main).DockArea.Windows);
        // One state window materializes as exactly one OS window (TW-9.14, DA-1.3) — a
        // duplicate would be the ghost: a tab strip whose single cached host lives elsewhere.
        var floating = workspace.WindowsTopMostFirst.OfType<Window>().Where(w => !ReferenceEquals(w, main)).ToArray();
        var document = Assert.Single(floating);
        Assert.Same(document, TopLevel.GetTopLevel(workspace.TabHosts.GetHost("d1")));
        // The nested command is not lost: the deferred re-run projects the live state.
        Assert.Equal(nested, St(main).DockArea.Windows[0].Bounds);
        Assert.Equal(new PixelPoint(220, 230), document.Position);
    }

    [AvaloniaFact]
    public void TW_9_14_command_during_projection_does_not_duplicate_pseudo_windows()
    {
        var (registry, lifecycle, content) = DockSetup();
        var workspace = new BerthWorkspace
        {
            ForceOverlayFloating = true,
            State = MainWindowTab(),
            Registry = registry,
            Lifecycle = lifecycle,
        };
        var main = new Window { Width = 800, Height = 600, Content = workspace };
        main.Show();
        Dispatcher.UIThread.RunJobs();
        var nested = new FloatingBounds(220, 230, 400, 300);
        content.Workspace = workspace;
        content.OnAttach = ws => ws.Execute(SetBounds(nested));

        workspace.Execute(s => s.MoveTabToNewWindow("d1", new FloatingBounds(50, 60, 400, 300)));
        Dispatcher.UIThread.RunJobs();

        Assert.True(content.FiredInsideProjection > 0);
        Assert.Single(St(main).DockArea.Windows);
        var canvas = (Canvas)Part(main, "PART_PseudoWindowLayer");
        var pseudos = canvas.Children.OfType<PseudoWindow>().Where(p => p.PanelId is null).ToArray();
        var document = Assert.Single(pseudos); // no ghost pseudo-window (TW-9.14)
        Assert.Equal(nested, St(main).DockArea.Windows[0].Bounds);
        Assert.Equal(220, Canvas.GetLeft(document));
        Assert.Equal(230, Canvas.GetTop(document));
    }

    [AvaloniaFact]
    public void TW_9_14_oscillating_commands_fail_loudly_instead_of_hanging()
    {
        var (registry, lifecycle, content) = DockSetup();
        var main = Show(MainWindowTab(), registry, lifecycle: lifecycle);
        var workspace = Workspace(main);
        content.Workspace = workspace;
        // The two-stroke cycle of the spec: every pass reattaches the host into the other
        // dock host, the attachment issues the opposite move — no fixed point exists, so
        // equal-state deduplication bounds nothing and only the pass cap can stop the loop.
        content.OnAttach = ws => ws.Execute(s =>
            !DockTrees.LayoutContainsTab(s, "d1")
                ? s
                : DockTrees.ContainsTab(s.DockArea.Root, "d1")
                    ? s.MoveTabToNewWindow("d1", new FloatingBounds(50, 60, 400, 300))
                    : s.MoveTab("d1", DockGroupRef.HostRoot(DockHost.MainWindow), int.MaxValue, registry));

        var oscillation = Assert.Throws<InvalidOperationException>(
            () => workspace.Execute(s => s.MoveTabToNewWindow("d1", new FloatingBounds(50, 60, 400, 300))));

        Assert.Contains("TW-9.14", oscillation.Message, StringComparison.Ordinal);
    }

    /// <summary>Panel body that answers its own focus gain by activating the rival panel — the DA-6.4-style reaction gone cyclic.</summary>
    private sealed class ActivateRivalOnFocus : TextBox
    {
        public BerthWorkspace? Workspace { get; set; }

        public string? Rival { get; set; }

        protected override void OnGotFocus(Avalonia.Input.FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            if (Workspace is { } workspace && Rival is { } rival)
            {
                workspace.Execute(s => s.Open(rival));
            }
        }
    }

    private sealed class BodyFactory(Func<string, object> create) : IToolWindowContentFactory
    {
        public object CreateContent(string toolWindowId) => create(toolWindowId);

        public void ReleaseContent(string toolWindowId, object content)
        {
        }
    }

    [AvaloniaFact]
    public void TW_9_14_focus_reaction_ping_pong_on_the_command_stack_fails_loudly()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var bodies = new Dictionary<string, ActivateRivalOnFocus>(StringComparer.Ordinal);
        var factory = new BodyFactory(id => bodies[id] = new ActivateRivalOnFocus());
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "a", "Alpha", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            ContentFactory = factory,
        });
        state = lifecycle.Register(state, new ToolWindowDescriptor(
            "b", "Beta", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary))
        {
            ContentFactory = factory,
        });
        state = state with { ToolWindows = [.. state.ToolWindows.Select(w => w with { IsOpen = true })] };
        var main = Show(state, registry, lifecycle: lifecycle);
        var workspace = Workspace(main);
        // The two-stroke cycle on the Execute stack, past the projection funnel: activation
        // transfers focus into the panel content (TW-6.6), whose focus gain activates the
        // rival — every nesting level runs its own bounded projection, so the pass cap
        // bounds nothing here; only the command-depth cap can stop the recursion before it
        // dies as an uncatchable stack overflow.
        bodies["a"].Workspace = workspace;
        bodies["a"].Rival = "b";
        bodies["b"].Workspace = workspace;
        bodies["b"].Rival = "a";

        var pingPong = Assert.Throws<InvalidOperationException>(() => workspace.Execute(s => s.Open("a")));

        // The command-depth cap fired, not the pass cap: the recursion runs past the
        // projection funnel, one complete bounded pass per nesting level.
        Assert.Contains("Commands nested deeper", pingPong.Message, StringComparison.Ordinal);
        Assert.Contains("TW-9.14", pingPong.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TW_9_14_registry_swap_during_projection_defers_the_cache_teardown()
    {
        var (registry, lifecycle, content) = DockSetup();
        var main = Show(MainWindowTab(), registry, lifecycle: lifecycle);
        var workspace = Workspace(main);
        var swapped = new ToolWindowRegistry();
        swapped.RegisterDockContent(new CountingTabFactory("d", _ => content));
        var swappedLifecycle = new ContentLifecycle(swapped);
        content.Workspace = workspace;
        content.OnAttach = ws =>
        {
            // One-shot: the reconfiguration mid-pass must defer the Reset to the funnel —
            // an immediate teardown would crash the running pass on its next cache access.
            content.OnAttach = null;
            ws.Lifecycle = swappedLifecycle;
            ws.Registry = swapped;
        };

        workspace.Execute(s => s.MoveTabToNewWindow("d1", new FloatingBounds(50, 60, 400, 300)));
        Dispatcher.UIThread.RunJobs();

        Assert.True(content.FiredInsideProjection > 0);
        Assert.Same(swapped, workspace.Registry);
        Assert.Single(St(main).DockArea.Windows);
        // The rebuilt projection over the new registry still materializes the one state
        // window as one OS window hosting the tab.
        var floating = workspace.WindowsTopMostFirst.OfType<Window>().Where(w => !ReferenceEquals(w, main)).ToArray();
        var document = Assert.Single(floating);
        Assert.Same(document, TopLevel.GetTopLevel(workspace.TabHosts.GetHost("d1")));
    }

    /// <summary>
    /// Tab content that acts when the projection detaches it — the deterministic stand-in for
    /// the real trigger of that same detach: the keyboard focus it held moves, and a focus
    /// reaction is a core command (DA-6.4, TW-6.1). One-shot, so the detaches of the deferred
    /// re-run stay quiet.
    /// </summary>
    private sealed class ActOnDetach : TextBox
    {
        public BerthWorkspace? Workspace { get; set; }

        public Action<BerthWorkspace>? OnDetach { get; set; }

        public int FiredInsideProjection { get; private set; }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (Workspace is { } workspace && OnDetach is { } act)
            {
                OnDetach = null;
                if (workspace.IsProjecting)
                {
                    FiredInsideProjection++;
                }

                act(workspace);
            }
        }
    }

    private static SplitNode Row(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Row, Children = [.. children] };

    private static MenuItem Item(ItemCollection items, string header) =>
        items.OfType<MenuItem>().First(i => string.Equals(i.Header as string, header, StringComparison.Ordinal));

    private static void Invoke(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TW_9_14_command_during_a_shrinking_split_keeps_the_pass_consistent()
    {
        // The reported desktop crash, end to end: a tab menu «Move to …» whose move empties
        // one group of a three-child split. The pass releases that group's view (TW-9.13,
        // DA-9.6) while it still holds the tab host — the receiver reattaches it only later
        // in the same pass — so the detach moves keyboard focus and the focus reaction issues
        // a command from inside the pass. Without the funnel of TW-9.14 the nested pass
        // rebuilt the split's child list under the running loop, and the pass died on a stale
        // index.
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new CountingTabFactory("d", _ => new TextBox()));
        var content = new ActOnDetach();
        registry.Register(new ToolWindowDescriptor(
            "p", "P", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            TabFactory = new CountingTabFactory("p:", _ => content),
        });
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            // The owner panel stays closed: its tree is not materialized, so no decorator
            // adopts the tab host before the dock area's own subpass reaches the split.
            ToolWindows = [Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary)],
            DockArea = new DockAreaState
            {
                Root = Row(
                    new SplitChild(Group("d1", "d1"), 0.4),
                    new SplitChild(Group("d2", "d2"), 0.3),
                    new SplitChild(Group("p:t1", "p:t1"), 0.3)),
                CurrentTabId = "d1",
            },
        };
        var main = Show(state, registry, lifecycle: lifecycle);
        var workspace = Workspace(main);
        content.Workspace = workspace;
        content.OnDetach = ws => ws.Execute(s => s.ActivateTab("d2"));

        // The reported entry point: the move item of DockTabMenus, with its activation and
        // focus transfer following (DA-5.4, DA-6.4).
        Invoke(Item(((MenuFlyout)TabHeader(main, "p:t1").ContextFlyout!).Items, "Move to P"));

        Assert.True(content.FiredInsideProjection > 0); // the command really ran inside a pass
        // The pass survived and converged: the split lost its third child, the moved tab
        // lives in its owner's tree, and the owner opened by the activation (DA-E39)...
        var root = Assert.IsType<SplitNode>(St(main).DockArea.Root);
        Assert.Equal(2, root.Children.Length);
        var panel = St(main).ToolWindows.Single(w => string.Equals(w.Id, "p", StringComparison.Ordinal));
        Assert.Equal(["p:t1"], Assert.IsType<TabGroupNode>(panel.ContentTree).Tabs);
        Assert.True(panel.IsOpen);
        // ...and the command raised mid-pass is not lost — the deferred re-run projected it.
        Assert.Equal("d2", St(main).DockArea.CurrentTabId);
        Assert.Empty(LayoutInvariants.Validate(St(main), registry));
    }
}
