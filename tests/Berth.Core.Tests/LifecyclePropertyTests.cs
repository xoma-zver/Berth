using CsCheck;
using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Property test of the content lifecycle (spec TW-9.2…TW-9.5, TW-9.11, DA-9.3, DA-9.4):
/// arbitrary sequences of layout commands — each reported to NotifyTransition — interleaved
/// with Register/Unregister/MaterializeTab/GetOrCreate keep the invariants of both specs plus
/// the liveness invariants: live content refers only to entities present in the layout, a
/// DisposeOnClose window that left the open state holds no body content unless the body tab
/// lives in a dock host (DA-8.3), and every factory's create/release counters balance after
/// the teardown. Panels carry content trees: bodies are seeded at registration (TW-9.5),
/// panel tabs open via OpenPanelTab and move across hosts under canHost (INV-D5). A shadow
/// model of expected liveness is maintained alongside and compared with the factories after
/// every step.
/// </summary>
public class LifecyclePropertyTests
{
    private static readonly FloatingBounds Bounds = new(10, 20, 800, 600);

    [Fact]
    public void Any_sequence_of_lifecycle_operations_preserves_invariants_and_liveness()
    {
        var scenario =
            from count in Gen.Int[0, 25]
            from ops in GenOp.Array[count]
            select ops;

        scenario.Sample(
            ops =>
            {
                var harness = new Harness();
                foreach (var op in ops)
                {
                    harness.Apply(op);
                    harness.Check();
                }

                harness.Teardown();
            },
            iter: 10_000);
    }

    private static readonly Gen<Op> GenOp =
        from kind in Gen.Int[0, 14]
        from a in Gen.Int[0, 1023]
        from b in Gen.Int[0, 1023]
        from c in Gen.Int[0, 1023]
        select new Op(kind, a, b, c);

    private sealed record Op(int Kind, int A, int B, int C);

    /// <summary>
    /// The system under test plus a shadow model: the harness applies one command, reports the
    /// transition (except for coordinator-driven ones, per the NotifyTransition contract),
    /// mirrors the expected liveness rules, and compares the mirror with the factory counters.
    /// </summary>
    private sealed class Harness
    {
        private static readonly (ContentCreationPolicy Creation, ContentRetentionPolicy Retention)[] Policies =
        [
            (ContentCreationPolicy.OnFirstOpen, ContentRetentionPolicy.KeepWhileRegistered),
            (ContentCreationPolicy.Eager, ContentRetentionPolicy.KeepWhileRegistered),
            (ContentCreationPolicy.OnFirstOpen, ContentRetentionPolicy.DisposeOnClose),
            (ContentCreationPolicy.Eager, ContentRetentionPolicy.DisposeOnClose),
        ];

        private readonly ToolWindowRegistry _registry = new();
        private readonly ContentLifecycle _lifecycle;
        private readonly StubTabFactory _docs = new("d");
        private readonly StubToolWindowFactory[] _panelFactories = new StubToolWindowFactory[4];
        private readonly StubTabFactory[] _tabFactories = new StubTabFactory[4];
        private readonly HashSet<string> _livePanels = new(StringComparer.Ordinal);
        private readonly HashSet<string> _liveTabs = new(StringComparer.Ordinal);
        private LayoutState _state = LayoutState.Empty;

        public Harness()
        {
            _lifecycle = new ContentLifecycle(_registry);
            _state = _lifecycle.RegisterDockContent(_state, _docs);
            for (var i = 0; i < 4; i++)
            {
                _panelFactories[i] = new StubToolWindowFactory();
                _tabFactories[i] = new StubTabFactory($"tw{i}:");
            }

            RegisterPanel(0);
            RegisterPanel(1);
        }

        public void Apply(Op op)
        {
            var panel = $"tw{op.A % 4}";
            var tabs = AllTabs(_state);
            string Tab(int seed) => tabs[seed % tabs.Count];

            switch (op.Kind)
            {
                case 0 when HasRecord(panel):
                    Transition(_state.Open(panel, (op.B & 1) == 0));
                    break;
                case 1 when HasRecord(panel):
                    Transition(_state.Close(panel));
                    break;
                case 2:
                    Transition(_state.HideAll());
                    break;
                case 3 when HasRecord(panel):
                    Transition(_state.SetIconVisible(panel, (op.B & 1) == 0));
                    break;
                case 4:
                {
                    var id = (op.A % 3) switch
                    {
                        0 => $"d{op.B % 6}",
                        1 => $"tw{op.B % 4}:t{op.C % 4}",
                        _ => $"x{op.B % 3}",
                    };

                    // Владелец решает точку входа: панельные вкладки — OpenPanelTab (TW-9.12),
                    // документы и незаявленные — OpenDocument (DA-5.1).
                    if (_registry.ResolveTabOwner(id)?.ToolWindowId is not null)
                    {
                        Transition(_state.OpenPanelTab(id, _registry));
                    }
                    else
                    {
                        Transition(_state.OpenDocument(id, _registry));
                    }

                    break;
                }

                case 5 when tabs.Count > 0:
                    Transition(_state.CloseTab(Tab(op.A)));
                    break;
                case 6 when tabs.Count > 0:
                    Transition(_state.ActivateTab(Tab(op.A)));
                    break;
                case 7 when tabs.Count > 0:
                {
                    var id = Tab(op.A);
                    var target = Tab(op.B);
                    // canHost (INV-D5): перенос в панель легален только для её подтверждённых
                    // вкладок; нелегальная комбинация — предусловие не выполнено, шаг пропущен.
                    var destinationPanel = PanelOf(_state, target);
                    var sourcePanel = PanelOf(_state, id);
                    if (destinationPanel is null
                        || string.Equals(sourcePanel, destinationPanel, StringComparison.Ordinal)
                        || _registry.ResolveTabOwner(id)?.ToolWindowId == destinationPanel)
                    {
                        Transition(_state.MoveTab(id, DockGroupRef.AtTab(target), op.C - 2, _registry));
                    }

                    break;
                }

                case 8 when tabs.Count > 0:
                    Transition(_state.SplitTab(Tab(op.A), (SplitDirection)(op.B % 4)));
                    break;
                case 9 when tabs.Count > 0:
                    Transition(_state.MoveTabToNewWindow(Tab(op.A), Bounds));
                    break;
                case 10:
                {
                    var rotatable = RotatableTabs(_state);
                    if (rotatable.Count > 0)
                    {
                        Transition(_state.RotateSplit(rotatable[op.A % rotatable.Count]));
                    }

                    break;
                }

                case 11 when !_registry.TryGet(panel, out _):
                    RegisterPanel(op.A % 4);
                    break;
                case 12 when _registry.TryGet(panel, out _):
                {
                    // Переход координатора: карты обновляет он сам, NotifyTransition не зовётся.
                    _state = _lifecycle.Unregister(_state, panel);
                    _livePanels.Remove(panel);
                    _liveTabs.RemoveWhere(t => t.StartsWith($"tw{op.A % 4}:", StringComparison.Ordinal));
                    break;
                }

                case 13 when tabs.Count > 0:
                {
                    var tab = Tab(op.A);
                    var result = _lifecycle.MaterializeTab(_state, tab);
                    _state = result.State;
                    if (result.Kind == TabMaterializationKind.Materialized)
                    {
                        // Тело панели живёт в карте панельного контента (мост TW-9.5).
                        if (_registry.TryGet(tab, out _))
                        {
                            _livePanels.Add(tab);
                        }
                        else
                        {
                            _liveTabs.Add(tab);
                        }
                    }

                    break;
                }

                case 14 when _registry.TryGet(panel, out _):
                    _lifecycle.GetOrCreateToolWindowContent(panel);
                    _livePanels.Add(panel);
                    break;
                default:
                    break; // предусловие не выполнено — команда невыразима, шаг пропущен
            }
        }

        public void Check()
        {
            Assert.Empty(LayoutInvariants.Validate(_state, _registry));

            for (var i = 0; i < 4; i++)
            {
                var id = $"tw{i}";
                Assert.Equal(_livePanels.Contains(id) ? 1 : 0, _panelFactories[i].LiveCount);
                Assert.Equal(
                    _liveTabs.Count(t => t.StartsWith($"tw{i}:", StringComparison.Ordinal)),
                    _tabFactories[i].LiveCount);
            }

            Assert.Equal(
                _liveTabs.Count(t => t.StartsWith("d", StringComparison.Ordinal)),
                _docs.LiveCount);

            // Живой контент вкладок ⊆ раскладка.
            var present = AllTabs(_state).ToHashSet(StringComparer.Ordinal);
            Assert.True(_liveTabs.IsSubsetOf(present), "live tab content must refer to tabs present in the layout");
        }

        public void Teardown()
        {
            _lifecycle.ReleaseAll();
            foreach (var factory in _panelFactories)
            {
                Assert.Equal(0, factory.LiveCount);
                Assert.Equal(factory.Created, factory.Released);
            }

            foreach (var factory in _tabFactories.Append(_docs))
            {
                Assert.Equal(0, factory.LiveCount);
                Assert.Equal(factory.Created, factory.Released);
            }
        }

        private void RegisterPanel(int index)
        {
            var id = $"tw{index}";
            var (creation, retention) = Policies[index];
            var slot = index < 2
                ? new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary)
                : new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary);
            _state = _lifecycle.Register(_state, new ToolWindowDescriptor(id, id, slot)
            {
                CreationPolicy = creation,
                RetentionPolicy = retention,
                ContentFactory = _panelFactories[index],
                TabFactory = _tabFactories[index],
            });
            if (creation == ContentCreationPolicy.Eager)
            {
                _livePanels.Add(id);
            }
        }

        /// <summary>One reported transition: apply, notify, mirror the release rules of TW-9.2/DA-5.4.</summary>
        private void Transition(LayoutState after)
        {
            var before = _state;
            _lifecycle.NotifyTransition(before, after);

            var presentBefore = AllTabs(before).ToHashSet(StringComparer.Ordinal);
            var presentAfter = AllTabs(after).ToHashSet(StringComparer.Ordinal);
            _livePanels.RemoveWhere(id =>
                (presentBefore.Contains(id) && !presentAfter.Contains(id))
                || (_registry.TryGet(id, out var descriptor)
                    && descriptor.RetentionPolicy == ContentRetentionPolicy.DisposeOnClose
                    && IsOpen(before, id)
                    && !IsOpen(after, id)
                    && !InDockHost(after, id)));
            _liveTabs.RemoveWhere(t => presentBefore.Contains(t) && !presentAfter.Contains(t));

            _state = after;
        }

        private bool HasRecord(string id) =>
            _state.ToolWindows.Any(w => string.Equals(w.Id, id, StringComparison.Ordinal));

        private static bool IsOpen(LayoutState state, string id) =>
            state.ToolWindows.Any(w => string.Equals(w.Id, id, StringComparison.Ordinal) && w.IsOpen);

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

        private static IEnumerable<TabTreeNode> DockRoots(LayoutState state)
        {
            yield return state.DockArea.Root;
            foreach (var window in state.DockArea.Windows)
            {
                yield return window.Root;
            }
        }

        private static IEnumerable<TabTreeNode> AllRoots(LayoutState state)
        {
            foreach (var root in DockRoots(state))
            {
                yield return root;
            }

            foreach (var panel in state.ToolWindows)
            {
                yield return panel.ContentTree;
            }
        }

        private static List<string> AllTabs(LayoutState state) =>
            AllRoots(state).SelectMany(Groups).SelectMany(g => g.Tabs).ToList();

        private static bool InDockHost(LayoutState state, string tab) =>
            DockRoots(state).SelectMany(Groups).Any(g => g.Tabs.Contains(tab, StringComparer.Ordinal));

        /// <summary>Id панели, в чьём дереве живёт вкладка, либо null для хостов док-зоны.</summary>
        private static string? PanelOf(LayoutState state, string tab) =>
            state.ToolWindows.FirstOrDefault(w =>
                Groups(w.ContentTree).Any(g => g.Tabs.Contains(tab, StringComparer.Ordinal)))?.Id;

        private static List<string> RotatableTabs(LayoutState state) =>
            AllRoots(state).Where(root => root is SplitNode)
                .SelectMany(Groups).SelectMany(g => g.Tabs).ToList();
    }
}
