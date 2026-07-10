using System.Collections.Immutable;
using CsCheck;
using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Property test for spec document-area sections 4–6 and tool-windows section 9: any sequence
/// of tree commands — over dock-area hosts and panel content trees alike — interleaved with
/// tool window open/close for the activity axis (DA-6.1/DA-6.2), keeps INV-D1…INV-D6 alongside
/// INV-1…INV-7. Command arguments are resolved against the current state by precondition
/// checks, not by catching exceptions: a command with no valid argument becomes a no-op; moves
/// into panels are generated only for owner-confirmed tabs (canHost, INV-D5). The tree is
/// walked through the public node types only.
/// </summary>
public class DockOperationsPropertyTests
{
    private static readonly FloatingBounds Bounds = new(10, 20, 800, 600);

    [Fact]
    public void Any_sequence_of_dock_operations_preserves_invariants()
    {
        var scenario =
            from opCount in Gen.Int[0, 30]
            from ops in GenOp().Array[opCount]
            select ops;

        scenario.Sample(
            ops =>
            {
                var (registry, state) = BuildStart();
                foreach (var op in ops)
                {
                    state = op.Apply(state, registry);
                    Assert.Empty(LayoutInvariants.Validate(state, registry));
                }
            },
            iter: 20_000);
    }

    /// <summary>
    /// Two panels with tab claims by prefix (tw0:/tw1:) and no body factories: the panel trees
    /// start empty and are populated by OpenPanelTab and owner-confirmed moves.
    /// </summary>
    private static (ToolWindowRegistry Registry, LayoutState State) BuildStart()
    {
        var registry = new ToolWindowRegistry();
        var left = new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary);
        var right = new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary);
        registry.Register(new ToolWindowDescriptor("tw0", "tw0", left)
        {
            TabFactory = new StubTabFactory("tw0:"),
        });
        registry.Register(new ToolWindowDescriptor("tw1", "tw1", right)
        {
            TabFactory = new StubTabFactory("tw1:"),
        });
        var state = LayoutState.Empty with
        {
            ToolWindows = [new ToolWindowState("tw0", left, 0), new ToolWindowState("tw1", right, 0)],
        };
        return (registry, state);
    }

    private static Gen<Op> GenOp()
    {
        var seed = Gen.Int[0, 1023];
        return Gen.OneOf(
            from s in seed select (Op)new OpenDocumentOp(s),
            from s in seed from t in seed select (Op)new OpenPanelTabOp(s, t),
            from s in seed select (Op)new CloseTabOp(s),
            from s in seed select (Op)new ActivateTabOp(s),
            from s in seed from t in seed from i in Gen.Int[-2, 6] select (Op)new MoveTabOp(s, t, i),
            from s in seed from d in Gen.Int[0, 3] select (Op)new SplitTabOp(s, d),
            from s in seed from v in seed select (Op)new SetSplitSharesOp(s, v),
            from s in seed select (Op)new MoveTabToNewWindowOp(s),
            from s in seed select (Op)new SetDocumentWindowBoundsOp(s),
            from s in seed select (Op)new RotateSplitOp(s),
            from s in seed select (Op)new OpenPanelOp(s),
            from s in seed select (Op)new ClosePanelOp(s));
    }

    // ---- state inspection over the public model ----

    private static IEnumerable<TabGroupNode> Groups(TabTreeNode node)
    {
        switch (node)
        {
            case TabGroupNode group:
                yield return group;
                break;
            case SplitNode split:
                foreach (var child in split.Children)
                {
                    foreach (var group in Groups(child.Node))
                    {
                        yield return group;
                    }
                }

                break;
        }
    }

    /// <summary>Every tree host: dock hosts by <see cref="DockHost"/>, panels by id.</summary>
    private static IEnumerable<(DockHost Dock, string? PanelId, TabTreeNode Root)> Hosts(LayoutState state)
    {
        yield return (DockHost.MainWindow, null, state.DockArea.Root);
        for (var i = 0; i < state.DockArea.Windows.Length; i++)
        {
            yield return (DockHost.DocumentWindow(i), null, state.DockArea.Windows[i].Root);
        }

        foreach (var panel in state.ToolWindows)
        {
            yield return (default, panel.Id, panel.ContentTree);
        }
    }

    private static List<string> AllTabs(LayoutState state) =>
        Hosts(state).SelectMany(h => Groups(h.Root)).SelectMany(g => g.Tabs).ToList();

    private static List<(string? PanelId, string Tab)> TabsWithPanels(LayoutState state) =>
        Hosts(state)
            .SelectMany(h => Groups(h.Root).SelectMany(g => g.Tabs).Select(t => (h.PanelId, t)))
            .ToList();

    /// <summary>Id панели, в чьём дереве живёт вкладка, либо null для хостов док-зоны.</summary>
    private static string? PanelOf(LayoutState state, string tab) =>
        TabsWithPanels(state).First(x => string.Equals(x.Tab, tab, StringComparison.Ordinal)).PanelId;

    private static void CollectSplits(
        TabTreeNode node, ImmutableArray<int> path, List<(ImmutableArray<int> Path, SplitNode Split)> found)
    {
        if (node is not SplitNode split)
        {
            return;
        }

        found.Add((path, split));
        for (var i = 0; i < split.Children.Length; i++)
        {
            CollectSplits(split.Children[i].Node, path.Add(i), found);
        }
    }

    // ---- commands with precondition-resolved arguments ----

    private abstract record Op
    {
        public abstract LayoutState Apply(LayoutState state, ToolWindowRegistry registry);
    }

    private sealed record OpenDocumentOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry) =>
            state.OpenDocument($"d{Seed % 8}", registry);
    }

    private sealed record OpenPanelTabOp(int Seed, int Tab) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry) =>
            state.OpenPanelTab($"tw{Seed % 2}:t{Tab % 4}", registry);
    }

    private sealed record CloseTabOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry)
        {
            var tabs = AllTabs(state);
            return tabs.Count == 0 ? state : state.CloseTab(tabs[Seed % tabs.Count]);
        }
    }

    private sealed record ActivateTabOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry)
        {
            var tabs = AllTabs(state);
            return tabs.Count == 0 ? state : state.ActivateTab(tabs[Seed % tabs.Count]);
        }
    }

    private sealed record MoveTabOp(int Seed, int Target, int Index) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry)
        {
            var tabs = AllTabs(state);
            if (tabs.Count == 0)
            {
                return state;
            }

            var id = tabs[Seed % tabs.Count];
            DockGroupRef target;
            string? destinationPanel;
            if (Target % 5 == 0 && state.DockArea.Root is TabGroupNode)
            {
                // Каждый пятый перенос — в корень главного окна, когда тот является группой.
                target = DockGroupRef.HostRoot(DockHost.MainWindow);
                destinationPanel = null;
            }
            else if (Target % 5 == 1
                && state.ToolWindows.First(w => w.Id == $"tw{Target % 2}").ContentTree is TabGroupNode)
            {
                // Либо в корень дерева панели (DA-1.3).
                destinationPanel = $"tw{Target % 2}";
                target = DockGroupRef.PanelRoot(destinationPanel);
            }
            else
            {
                var targetTab = tabs[Target % tabs.Count];
                target = DockGroupRef.AtTab(targetTab);
                destinationPanel = PanelOf(state, targetTab);
            }

            // canHost (INV-D5): перенос в панель — только для её подтверждённых вкладок.
            var sourcePanel = PanelOf(state, id);
            if (destinationPanel is not null
                && !string.Equals(sourcePanel, destinationPanel, StringComparison.Ordinal)
                && registry.ResolveTabOwner(id)?.ToolWindowId != destinationPanel)
            {
                return state;
            }

            return state.MoveTab(id, target, Index, registry);
        }
    }

    private sealed record SplitTabOp(int Seed, int Direction) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry)
        {
            var tabs = AllTabs(state);
            return tabs.Count == 0
                ? state
                : state.SplitTab(tabs[Seed % tabs.Count], (SplitDirection)Direction);
        }
    }

    private sealed record SetSplitSharesOp(int Seed, int Variation) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry)
        {
            var splits = new List<(DockHost Dock, string? PanelId, ImmutableArray<int> Path, SplitNode Split)>();
            foreach (var (dock, panelId, root) in Hosts(state))
            {
                var found = new List<(ImmutableArray<int> Path, SplitNode Split)>();
                CollectSplits(root, [], found);
                splits.AddRange(found.Select(f => (dock, panelId, f.Path, f.Split)));
            }

            if (splits.Count == 0)
            {
                return state;
            }

            var (targetDock, targetPanel, path, split) = splits[Seed % splits.Count];
            var count = split.Children.Length;
            var weights = new double[count];
            var sum = 0.0;
            for (var i = 0; i < count; i++)
            {
                weights[i] = 1 + ((Variation + (i * 7)) % 5);
                sum += weights[i];
            }

            var shares = ImmutableArray.CreateBuilder<double>(count);
            var rest = 1.0;
            for (var i = 0; i < count - 1; i++)
            {
                var share = weights[i] / sum;
                shares.Add(share);
                rest -= share;
            }

            shares.Add(rest);
            return targetPanel is null
                ? state.SetSplitShares(targetDock, path, shares.ToImmutable())
                : state.SetSplitShares(targetPanel, path, shares.ToImmutable());
        }
    }

    private sealed record MoveTabToNewWindowOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry)
        {
            var tabs = AllTabs(state);
            return tabs.Count == 0 ? state : state.MoveTabToNewWindow(tabs[Seed % tabs.Count], Bounds);
        }
    }

    private sealed record SetDocumentWindowBoundsOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry)
        {
            var windowTabs = new List<string>();
            foreach (var window in state.DockArea.Windows)
            {
                windowTabs.AddRange(Groups(window.Root).SelectMany(g => g.Tabs));
            }

            return windowTabs.Count == 0
                ? state
                : state.SetDocumentWindowBounds(
                    windowTabs[Seed % windowTabs.Count], new FloatingBounds(Seed, Seed, 100 + Seed, 100));
        }
    }

    private sealed record RotateSplitOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry)
        {
            // Поворот определён только для вкладки, чья группа не является корнем хоста.
            var candidates = Hosts(state)
                .Where(h => h.Root is SplitNode)
                .SelectMany(h => Groups(h.Root))
                .SelectMany(g => g.Tabs)
                .ToList();
            return candidates.Count == 0 ? state : state.RotateSplit(candidates[Seed % candidates.Count]);
        }
    }

    private sealed record OpenPanelOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry) =>
            state.Open(Seed % 2 == 0 ? "tw0" : "tw1");
    }

    private sealed record ClosePanelOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state, ToolWindowRegistry registry) =>
            state.Close(Seed % 2 == 0 ? "tw0" : "tw1");
    }
}
