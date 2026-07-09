using System.Collections.Immutable;
using CsCheck;
using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Property test for spec document-area sections 4–6: any sequence of dock-area commands,
/// interleaved with tool window open/close for the activity axis (DA-6.1/DA-6.2), keeps
/// INV-D1…INV-D6 alongside INV-1…INV-7. Command arguments are resolved against the current
/// state by precondition checks, not by catching exceptions: a command with no valid argument
/// becomes a no-op. The tree is walked through the public node types only.
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
                    state = op.Apply(state);
                    Assert.Empty(LayoutInvariants.Validate(state, registry));
                }
            },
            iter: 20_000);
    }

    private static (ToolWindowRegistry Registry, LayoutState State) BuildStart()
    {
        var registry = new ToolWindowRegistry();
        var left = new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary);
        var right = new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary);
        registry.Register(new ToolWindowDescriptor("tw0", "tw0", left));
        registry.Register(new ToolWindowDescriptor("tw1", "tw1", right));
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

    private static IEnumerable<(DockHost Host, TabTreeNode Root)> Hosts(DockAreaState area)
    {
        yield return (DockHost.MainWindow, area.Root);
        for (var i = 0; i < area.Windows.Length; i++)
        {
            yield return (DockHost.DocumentWindow(i), area.Windows[i].Root);
        }
    }

    private static List<string> AllTabs(DockAreaState area) =>
        Hosts(area).SelectMany(h => Groups(h.Root)).SelectMany(g => g.Tabs).ToList();

    private static List<(DockHost Host, string Tab)> TabsWithHosts(DockAreaState area) =>
        Hosts(area).SelectMany(h => Groups(h.Root).SelectMany(g => g.Tabs).Select(t => (h.Host, t))).ToList();

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
        public abstract LayoutState Apply(LayoutState state);
    }

    private sealed record OpenDocumentOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.OpenDocument($"d{Seed % 8}");
    }

    private sealed record CloseTabOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state)
        {
            var tabs = AllTabs(state.DockArea);
            return tabs.Count == 0 ? state : state.CloseTab(tabs[Seed % tabs.Count]);
        }
    }

    private sealed record ActivateTabOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state)
        {
            var tabs = AllTabs(state.DockArea);
            return tabs.Count == 0 ? state : state.ActivateTab(tabs[Seed % tabs.Count]);
        }
    }

    private sealed record MoveTabOp(int Seed, int Target, int Index) : Op
    {
        public override LayoutState Apply(LayoutState state)
        {
            var tabs = AllTabs(state.DockArea);
            if (tabs.Count == 0)
            {
                return state;
            }

            var id = tabs[Seed % tabs.Count];
            // Каждый четвёртый перенос — в корень главного окна, когда тот является группой.
            var target = Target % 4 == 0 && state.DockArea.Root is TabGroupNode
                ? DockGroupRef.HostRoot(DockHost.MainWindow)
                : DockGroupRef.AtTab(tabs[Target % tabs.Count]);
            return state.MoveTab(id, target, Index);
        }
    }

    private sealed record SplitTabOp(int Seed, int Direction) : Op
    {
        public override LayoutState Apply(LayoutState state)
        {
            var tabs = AllTabs(state.DockArea);
            return tabs.Count == 0
                ? state
                : state.SplitTab(tabs[Seed % tabs.Count], (SplitDirection)Direction);
        }
    }

    private sealed record SetSplitSharesOp(int Seed, int Variation) : Op
    {
        public override LayoutState Apply(LayoutState state)
        {
            var splits = new List<(DockHost Host, ImmutableArray<int> Path, SplitNode Split)>();
            foreach (var (host, root) in Hosts(state.DockArea))
            {
                var found = new List<(ImmutableArray<int> Path, SplitNode Split)>();
                CollectSplits(root, [], found);
                splits.AddRange(found.Select(f => (host, f.Path, f.Split)));
            }

            if (splits.Count == 0)
            {
                return state;
            }

            var (targetHost, path, split) = splits[Seed % splits.Count];
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
            return state.SetSplitShares(targetHost, path, shares.ToImmutable());
        }
    }

    private sealed record MoveTabToNewWindowOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state)
        {
            var tabs = AllTabs(state.DockArea);
            return tabs.Count == 0 ? state : state.MoveTabToNewWindow(tabs[Seed % tabs.Count], Bounds);
        }
    }

    private sealed record SetDocumentWindowBoundsOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state)
        {
            var windowTabs = TabsWithHosts(state.DockArea).Where(t => !t.Host.IsMainWindow).ToList();
            return windowTabs.Count == 0
                ? state
                : state.SetDocumentWindowBounds(
                    windowTabs[Seed % windowTabs.Count].Tab, new FloatingBounds(Seed, Seed, 100 + Seed, 100));
        }
    }

    private sealed record RotateSplitOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state)
        {
            // Поворот определён только для вкладки, чья группа не является корнем хоста.
            var candidates = Hosts(state.DockArea)
                .Where(h => h.Root is SplitNode)
                .SelectMany(h => Groups(h.Root))
                .SelectMany(g => g.Tabs)
                .ToList();
            return candidates.Count == 0 ? state : state.RotateSplit(candidates[Seed % candidates.Count]);
        }
    }

    private sealed record OpenPanelOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.Open(Seed % 2 == 0 ? "tw0" : "tw1");
    }

    private sealed record ClosePanelOp(int Seed) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.Close(Seed % 2 == 0 ? "tw0" : "tw1");
    }
}
