using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CsCheck;
using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Property tests of persistence (spec TW-10.1, TW-10.4, DA-9.2): any state produced by core
/// operations survives Serialize → Deserialize → Apply without a single fix (the round-trip
/// invariant), any garbage snapshot is repaired into a valid state idempotently, and a
/// reflective serializer acts as an independent witness that the hand-written mapper covers
/// every public field — with a canary proving the witness itself sees deep tree differences.
/// </summary>
public class ApplyPropertyTests
{
    private static readonly FloatingBounds Bounds = new(10, 20, 800, 600);

    // ---- round trip over states produced by core operations ----

    [Fact]
    public void TW_10_4_DA_9_2_round_trip_of_core_states_is_fix_free()
    {
        var scenario =
            from count in Gen.Int[0, 25]
            from ops in GenOp.Array[count]
            select ops;

        scenario.Sample(
            ops =>
            {
                var (registry, state) = BuildStart();
                foreach (var op in ops)
                {
                    state = op.Apply(state);
                }

                var json = LayoutPersistence.Serialize(state);
                var result = LayoutState.Empty.Apply(
                    LayoutPersistence.Deserialize(json), ApplyScope.Full, registry);

                Assert.Empty(result.Fixes);
                Assert.Equal(json, LayoutPersistence.Serialize(result.State));
                Assert.Equal(Witness(state), Witness(result.State));
            },
            iter: 3_000);
    }

    [Fact]
    public void TW_10_1_every_field_of_the_rich_state_survives_the_round_trip()
    {
        var state = PersistenceTests.RichState();

        var result = LayoutState.Empty.Apply(
            LayoutPersistence.Deserialize(LayoutPersistence.Serialize(state)),
            ApplyScope.Full,
            new ToolWindowRegistry());

        Assert.Empty(result.Fixes);
        Assert.Equal(Witness(state), Witness(result.State));
    }

    [Fact]
    public void Witness_canary_sees_a_difference_deep_inside_a_tree()
    {
        // Проверка проверяющего: без явного полиморфизма рефлексивный сериализатор писал бы
        // TabTreeNode пустым объектом и слеп бы ровно на деревьях.
        var baseline = LayoutState.Empty
            .OpenDocument("x")
            .OpenDocument("y")
            .SplitTab("y", SplitDirection.Right);
        var reshared = baseline.SetSplitShares(DockHost.MainWindow, [], [0.4, 0.6]);

        Assert.NotEqual(Witness(baseline), Witness(reshared));
    }

    // ---- garbage snapshots ----

    [Fact]
    public void TW_10_4_DA_9_2_apply_of_any_garbage_snapshot_is_valid_and_idempotent()
    {
        GenGarbage.Sample(
            snapshot =>
            {
                var registry = new ToolWindowRegistry();
                registry.Register(new ToolWindowDescriptor(
                    "reg0", "reg0", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary)));

                var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, registry);
                Assert.Empty(LayoutInvariants.Validate(result.State, registry));

                var again = LayoutState.Empty.Apply(result.State, ApplyScope.Full, registry);
                Assert.Empty(again.Fixes);
                Assert.Equal(
                    LayoutPersistence.Serialize(result.State),
                    LayoutPersistence.Serialize(again.State));
            },
            iter: 10_000);
    }

    // ---- the reflective witness ----

    private static readonly JsonSerializerOptions WitnessOptions = CreateWitnessOptions();

    private static string Witness(LayoutState state) => JsonSerializer.Serialize(state, WitnessOptions);

    /// <summary>
    /// The reflective serializer is a test-only, independent witness of the hand-written
    /// mapper's field coverage: it enumerates every public property itself, so a field
    /// forgotten in LayoutPersistence shows up as a witness difference after the round trip.
    /// TabTreeNode needs explicit polymorphism — serialized by its declared type the abstract
    /// base has no members, and the witness would silently stop seeing the trees (the canary
    /// test guards exactly this).
    /// </summary>
    private static JsonSerializerOptions CreateWitnessOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type == typeof(TabTreeNode))
            {
                var polymorphism = new JsonPolymorphismOptions();
                polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(TabGroupNode), "group"));
                polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(SplitNode), "split"));
                typeInfo.PolymorphismOptions = polymorphism;
            }
        });
        return new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
    }

    // ---- generators: sequences of core operations ----

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

    private static readonly Gen<Op> GenOp =
        from kind in Gen.Int[0, 17]
        from a in Gen.Int[0, 1023]
        from b in Gen.Int[0, 1023]
        from c in Gen.Int[0, 1023]
        select new Op(kind, a, b, c);

    /// <summary>
    /// One core command with seed-resolved arguments: tool window commands map seeds onto the
    /// two known ids and valid values; dock commands resolve their arguments against the
    /// current state by precondition (no valid argument → no-op), as in
    /// DockOperationsPropertyTests. Together the commands exercise every serialized field.
    /// </summary>
    private sealed record Op(int Kind, int A, int B, int C)
    {
        public LayoutState Apply(LayoutState state)
        {
            var id = $"tw{A % 2}";
            var fraction = 0.05 + ((C % 90) / 100.0);
            var tabs = AllTabs(state.DockArea);
            string Tab(int seed) => tabs[seed % tabs.Count];

            switch (Kind)
            {
                case 0:
                    return state.Open(id, (B & 1) == 0);
                case 1:
                    return state.Close(id);
                case 2:
                    return state.SetMode(id, (ToolWindowMode)(B % 5), (C & 1) == 0 ? Bounds : null);
                case 3:
                    return state.Move(id, ToolWindowSlot.All[B % ToolWindowSlot.All.Length], C % 4);
                case 4:
                    return state.SetIconVisible(id, (B & 1) == 0);
                case 5:
                    return state.SetQuickAccessSide((B & 1) == 0 ? QuickAccessSide.Left : QuickAccessSide.Right);
                case 6:
                    return state.SetSideSize((ToolWindowSide)(B % 3), fraction);
                case 7:
                    return state.SetSideRatio((ToolWindowSide)(B % 3), fraction);
                case 8:
                    return state.SetUndockWeight(id, fraction);
                case 9:
                    return state.SetFloatingBounds(id, new FloatingBounds(A, B, 100 + C, 200));
                case 10:
                    return state.OpenDocument($"d{A % 8}");
                case 11:
                    return tabs.Count == 0 ? state : state.CloseTab(Tab(A));
                case 12:
                    return tabs.Count == 0 ? state : state.ActivateTab(Tab(A));
                case 13:
                    return tabs.Count == 0 ? state : state.MoveTab(Tab(A), DockGroupRef.AtTab(Tab(B)), C - 2);
                case 14:
                    return tabs.Count == 0 ? state : state.SplitTab(Tab(A), (SplitDirection)(B % 4));
                case 15:
                    return tabs.Count == 0 ? state : state.MoveTabToNewWindow(Tab(A), Bounds);
                case 16:
                {
                    var windowTabs = TabsInWindows(state.DockArea);
                    return windowTabs.Count == 0
                        ? state
                        : state.SetDocumentWindowBounds(
                            windowTabs[A % windowTabs.Count], new FloatingBounds(A, B, 100 + C, 150));
                }

                case 17:
                {
                    var rotatable = RotatableTabs(state.DockArea);
                    return rotatable.Count == 0 ? state : state.RotateSplit(rotatable[A % rotatable.Count]);
                }

                default:
                    return state;
            }
        }
    }

    // ---- generators: garbage snapshots ----

    private static readonly Gen<double> GenBadFraction = Gen.OneOf(
        Gen.Double[0.05, 0.95],
        Gen.Const(double.NaN),
        Gen.Const(0.0),
        Gen.Const(1.5),
        Gen.Const(-0.3));

    private static readonly Gen<ToolWindowMode> GenMode = Gen.OneOfConst(
        ToolWindowMode.DockPinned, ToolWindowMode.DockUnpinned, ToolWindowMode.Undock,
        ToolWindowMode.Float, ToolWindowMode.Window);

    private static readonly Gen<ToolWindowState> GenGarbageWindow =
        from id in Gen.Int[0, 4]
        from slot in Gen.Int[0, ToolWindowSlot.All.Length - 1]
        from order in Gen.Int[-2, 6]
        from mode in GenMode
        from last in GenMode
        from isOpen in Gen.Bool
        from icon in Gen.Bool
        from pair in GenBadFraction
        from undock in GenBadFraction
        from boundsKind in Gen.Int[0, 2]
        select new ToolWindowState($"tw{id}", ToolWindowSlot.All[slot], 0) with
        {
            Order = order,
            Mode = mode,
            LastInternalMode = last,
            IsOpen = isOpen,
            IsIconVisible = icon,
            PairRatio = pair,
            UndockWeight = undock,
            FloatingBounds = boundsKind switch
            {
                0 => null,
                1 => new FloatingBounds(1, 2, 300, 200),
                _ => new FloatingBounds(double.NaN, 0, 10, 10),
            },
        };

    /// <summary>
    /// Garbage trees over a small tab pool, so duplicates — within a group, between groups and
    /// between hosts — are frequent, together with empty groups, degenerate splits,
    /// same-orientation nesting, bad shares and dangling actives.
    /// </summary>
    private static Gen<TabTreeNode> GenGarbageNode(int depth)
    {
        var group =
            from count in Gen.Int[0, 3]
            from ids in Gen.Int[0, 7].Array[count]
            from activeKind in Gen.Int[0, count + 1]
            select (TabTreeNode)new TabGroupNode
            {
                Tabs = [.. ids.Select(i => $"d{i}")],
                ActiveTabId = activeKind < count ? $"d{ids[activeKind]}" : activeKind == count ? null : "ghost",
            };
        if (depth <= 0)
        {
            return group;
        }

        var child =
            from node in GenGarbageNode(depth - 1)
            from share in GenBadFraction
            select new SplitChild(node, share);
        var split =
            from orientation in Gen.OneOfConst(SplitOrientation.Row, SplitOrientation.Column)
            from count in Gen.Int[0, 3]
            from children in child.Array[count]
            select (TabTreeNode)new SplitNode { Orientation = orientation, Children = [.. children] };
        return Gen.OneOf(group, group, split);
    }

    private static readonly Gen<DockAreaState> GenGarbageArea =
        from mainRoot in GenGarbageNode(3)
        from mainCurrentKind in Gen.Int[0, 2]
        from windowCount in Gen.Int[0, 2]
        from windowRoots in GenGarbageNode(2).Array[windowCount]
        from windowCurrentKinds in Gen.Int[1, 2].Array[windowCount]
        from activeChoice in Gen.Int[0, 3]
        select BuildGarbageArea(mainRoot, mainCurrentKind, windowRoots, windowCurrentKinds, activeChoice);

    private static readonly Gen<LayoutState> GenGarbage =
        from windowCount in Gen.Int[0, 5]
        from windows in GenGarbageWindow.Array[windowCount]
        from leftWeight in GenBadFraction
        from leftRatio in GenBadFraction
        from rightWeight in GenBadFraction
        from rightRatio in GenBadFraction
        from bottomWeight in GenBadFraction
        from bottomRatio in GenBadFraction
        from quickAccess in Gen.OneOfConst(QuickAccessSide.Left, QuickAccessSide.Right)
        from activeKind in Gen.Int[0, 2]
        from area in GenGarbageArea
        select LayoutState.Empty with
        {
            ToolWindows = [.. windows],
            Left = new SideState(leftWeight, leftRatio),
            Right = new SideState(rightWeight, rightRatio),
            Bottom = new SideState(bottomWeight, bottomRatio),
            QuickAccessSide = quickAccess,
            ActiveToolWindowId = activeKind switch { 0 => null, 1 => "tw0", _ => "ghost" },
            DockArea = area,
        };

    private static DockAreaState BuildGarbageArea(
        TabTreeNode mainRoot, int mainCurrentKind, TabTreeNode[] windowRoots, int[] currentKinds, int activeChoice)
    {
        var windows = ImmutableArray.CreateBuilder<DocumentWindowState>(windowRoots.Length);
        for (var i = 0; i < windowRoots.Length; i++)
        {
            // Bounds окон держим конечными: у них нет правила починки в ядре — нечисловые
            // значения на файловом пути отвергает Deserialize (TW-10.5, DA-9.5).
            var current = (currentKinds[i] == 1 ? FirstTab(windowRoots[i]) : null) ?? "ghost";
            windows.Add(new DocumentWindowState(Bounds, windowRoots[i], current));
        }

        return new DockAreaState
        {
            Root = mainRoot,
            CurrentTabId = mainCurrentKind switch { 0 => null, 1 => FirstTab(mainRoot), _ => "ghost" },
            Windows = windows.ToImmutable(),
            ActiveDockHost = activeChoice == 0 ? DockHost.MainWindow : DockHost.DocumentWindow(activeChoice - 1),
        };
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

    private static List<string> TabsInWindows(DockAreaState area) =>
        Hosts(area).Where(h => !h.Host.IsMainWindow)
            .SelectMany(h => Groups(h.Root)).SelectMany(g => g.Tabs).ToList();

    private static List<string> RotatableTabs(DockAreaState area) =>
        Hosts(area).Where(h => h.Root is SplitNode)
            .SelectMany(h => Groups(h.Root)).SelectMany(g => g.Tabs).ToList();

    private static string? FirstTab(TabTreeNode node) => node switch
    {
        TabGroupNode group => group.Tabs.IsEmpty ? null : group.Tabs[0],
        SplitNode split => split.Children.Select(c => FirstTab(c.Node)).FirstOrDefault(t => t is not null),
        _ => null,
    };
}
