using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Snapshot application and defaults (spec TW-5.14): reconciliation TW-10.3, normalization
/// TW-10.4 with the fix report, bounds validation TW-7.4, the Arrangement merge TW-10.6/10.7,
/// ResetToDefaults; edge cases E11, E14, E20, E23…E25 (E12 — PersistenceTests).
/// </summary>
public class ApplyTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);
    private static readonly ToolWindowSlot RightPrimary = new(ToolWindowSide.Right, ToolWindowGroup.Primary);
    private static readonly ToolWindowRegistry EmptyRegistry = new();

    private static ToolWindowState Window(string id, ToolWindowSlot slot, int order) => new(id, slot, order);

    private static LayoutState Layout(params ToolWindowState[] windows) =>
        LayoutState.Empty with { ToolWindows = [.. windows] };

    private static ToolWindowState Get(LayoutState state, string id) =>
        state.ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static ToolWindowRegistry Registry(params ToolWindowDescriptor[] descriptors)
    {
        var registry = new ToolWindowRegistry();
        foreach (var descriptor in descriptors)
        {
            registry.Register(descriptor);
        }

        return registry;
    }

    private static string[] FixRules(ApplyResult result) => result.Fixes.Select(f => f.Rule).ToArray();

    // ---- TW-10.3 reconciliation ----

    [Fact]
    public void TW_10_3_saved_state_wins_over_the_descriptor()
    {
        var registry = Registry(
            new ToolWindowDescriptor("a", "A", RightPrimary) { DefaultMode = ToolWindowMode.Undock });
        var snapshot = Layout(Window("a", LeftPrimary, 0));

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, registry);

        Assert.Equal(LeftPrimary, Get(result.State, "a").Slot);
        Assert.Equal(ToolWindowMode.DockPinned, Get(result.State, "a").Mode);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void TW_10_3_registered_but_not_saved_gets_defaults_after_the_existing()
    {
        var registry = Registry(
            new ToolWindowDescriptor("a", "A", LeftPrimary) { DefaultPairRatio = 0.7 },
            new ToolWindowDescriptor("b", "B", LeftPrimary));
        var snapshot = Layout(Window("x", LeftPrimary, 0)); // спящий жилец слота

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, registry);

        Assert.Equal(0, Get(result.State, "x").Order);
        Assert.Equal(1, Get(result.State, "a").Order);
        Assert.Equal(2, Get(result.State, "b").Order);
        Assert.False(Get(result.State, "a").IsOpen);
        Assert.Equal(0.7, Get(result.State, "a").PairRatio);
        Assert.Empty(result.Fixes); // примирение — штатное поведение, не починка
        Assert.Empty(LayoutInvariants.Validate(result.State, registry));
    }

    // ---- TW-10.4 normalization with the report ----

    [Fact]
    public void TW_10_4_duplicate_id_keeps_the_first_occurrence()
    {
        var snapshot = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true, PairRatio = 0.7 },
            Window("a", RightPrimary, 0));

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, new ToolWindowRegistry());

        var survivor = Assert.Single(result.State.ToolWindows);
        Assert.Equal(LeftPrimary, survivor.Slot);
        Assert.Equal(0.7, survivor.PairRatio);
        Assert.Equal(["INV-1"], FixRules(result));
    }

    [Fact]
    public void TW_10_4_E11_docked_conflict_keeps_the_window_with_the_min_order()
    {
        var snapshot = Layout(
            Window("a", LeftPrimary, 1) with { IsOpen = true },
            Window("b", LeftPrimary, 0) with { IsOpen = true });

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, new ToolWindowRegistry());

        Assert.False(Get(result.State, "a").IsOpen);
        Assert.True(Get(result.State, "b").IsOpen);
        Assert.Equal(["INV-2"], FixRules(result));
    }

    [Fact]
    public void TW_10_4_overlay_conflict_is_repaired_independently_of_the_docked_layer()
    {
        var snapshot = Layout(
            Window("d", LeftPrimary, 0) with { IsOpen = true },
            Window("u1", LeftPrimary, 1) with
            {
                IsOpen = true,
                Mode = ToolWindowMode.Undock,
                LastInternalMode = ToolWindowMode.Undock,
            },
            Window("u2", LeftPrimary, 2) with
            {
                IsOpen = true,
                Mode = ToolWindowMode.Undock,
                LastInternalMode = ToolWindowMode.Undock,
            });

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, new ToolWindowRegistry());

        Assert.True(Get(result.State, "d").IsOpen); // докированный слой не тронут (INV-2)
        Assert.True(Get(result.State, "u1").IsOpen);
        Assert.False(Get(result.State, "u2").IsOpen);
        Assert.Equal(["INV-2"], FixRules(result));
    }

    [Fact]
    public void TW_10_4_orders_are_compacted_stably()
    {
        var snapshot = Layout(Window("a", LeftPrimary, 3), Window("b", LeftPrimary, 7));

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, new ToolWindowRegistry());

        Assert.Equal(0, Get(result.State, "a").Order);
        Assert.Equal(1, Get(result.State, "b").Order);
        Assert.Equal(["INV-3"], FixRules(result));
    }

    [Fact]
    public void TW_10_4_invalid_fractions_become_field_defaults()
    {
        var snapshot = Layout(
                Window("a", LeftPrimary, 0) with { PairRatio = double.NaN, UndockWeight = 1.5 })
            with { Left = new SideState(Weight: 0.0) };

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, new ToolWindowRegistry());

        Assert.Equal(LayoutDefaults.PairRatio, Get(result.State, "a").PairRatio);
        Assert.Equal(LayoutDefaults.UndockWeight, Get(result.State, "a").UndockWeight);
        Assert.Equal(LayoutDefaults.SideWeight, result.State.Left.Weight);
        Assert.Equal(["INV-4", "INV-4", "INV-4"], FixRules(result));
    }

    [Fact]
    public void TW_10_4_dangling_or_closed_active_id_becomes_null()
    {
        var dangling = Layout(Window("a", LeftPrimary, 0)) with { ActiveToolWindowId = "ghost" };
        var closed = Layout(Window("a", LeftPrimary, 0)) with { ActiveToolWindowId = "a" };

        var danglingResult = LayoutState.Empty.Apply(dangling, ApplyScope.Full, new ToolWindowRegistry());
        var closedResult = LayoutState.Empty.Apply(closed, ApplyScope.Full, new ToolWindowRegistry());

        Assert.Null(danglingResult.State.ActiveToolWindowId);
        Assert.Equal(["INV-5"], FixRules(danglingResult));
        Assert.Null(closedResult.State.ActiveToolWindowId);
        Assert.Equal(["INV-5"], FixRules(closedResult));
    }

    [Fact]
    public void TW_10_4_active_on_the_evicted_window_clears_without_its_own_entry()
    {
        // Актив ссылался на открытую панель; её закрыл фикс INV-2 — сброс актива вторичен
        // и отдельной записи не порождает (TW-10.4).
        var snapshot = Layout(
            Window("a", LeftPrimary, 1) with { IsOpen = true },
            Window("b", LeftPrimary, 0) with { IsOpen = true }) with { ActiveToolWindowId = "a" };

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, new ToolWindowRegistry());

        Assert.Null(result.State.ActiveToolWindowId);
        Assert.Equal(["INV-2"], FixRules(result));
    }

    [Fact]
    public void TW_10_4_open_window_with_a_hidden_icon_gets_the_icon()
    {
        var snapshot = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true, IsIconVisible = false });

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, new ToolWindowRegistry());

        Assert.True(Get(result.State, "a").IsOpen);
        Assert.True(Get(result.State, "a").IsIconVisible);
        Assert.Equal(["INV-6"], FixRules(result));
    }

    [Fact]
    public void TW_10_4_last_internal_mode_is_repaired()
    {
        var snapshot = Layout(
            Window("a", LeftPrimary, 0) with
            {
                Mode = ToolWindowMode.DockUnpinned,
                LastInternalMode = ToolWindowMode.DockPinned,
            },
            Window("b", RightPrimary, 0) with
            {
                Mode = ToolWindowMode.Float,
                LastInternalMode = ToolWindowMode.Window,
            });

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, new ToolWindowRegistry());

        Assert.Equal(ToolWindowMode.DockUnpinned, Get(result.State, "a").LastInternalMode);
        Assert.Equal(ToolWindowMode.DockPinned, Get(result.State, "b").LastInternalMode);
        Assert.Equal(["INV-7", "INV-7"], FixRules(result));
    }

    // ---- TW-7.4 bounds validation ----

    [Fact]
    public void TW_7_4_E14_offscreen_bounds_are_replaced_by_the_validator()
    {
        var replacement = new FloatingBounds(50, 50, 300, 200);
        var snapshot = Layout(Window("a", LeftPrimary, 0) with
        {
            Mode = ToolWindowMode.Float,
            FloatingBounds = new FloatingBounds(5000, 5000, 100, 100),
        });

        var result = LayoutState.Empty.Apply(
            snapshot, ApplyScope.Full, new ToolWindowRegistry(),
            bounds => bounds.X > 1000 ? replacement : null);

        Assert.Equal(replacement, Get(result.State, "a").FloatingBounds);
        Assert.Equal(["TW-7.4"], FixRules(result));
    }

    [Fact]
    public void TW_10_4_non_numeric_bounds_reset_to_null_without_calling_the_validator()
    {
        var calls = 0;
        var snapshot = Layout(Window("a", LeftPrimary, 0) with
        {
            FloatingBounds = new FloatingBounds(double.NaN, 0, 10, 10),
        });

        var result = LayoutState.Empty.Apply(
            snapshot, ApplyScope.Full, new ToolWindowRegistry(),
            _ => { calls++; return null; });

        Assert.Null(Get(result.State, "a").FloatingBounds);
        Assert.Equal(0, calls);
        Assert.Equal(["TW-10.4"], FixRules(result));
    }

    // ---- TW-10.6 / TW-10.7 Arrangement ----

    [Fact]
    public void TW_10_6_E20_arrangement_leaves_the_dock_area_untouched()
    {
        var current = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true })
            .OpenDocument("doc1", EmptyRegistry)
            .OpenDocument("doc2", EmptyRegistry);
        var macro = Layout(Window("a", RightPrimary, 0) with { IsOpen = true });

        var result = current.Apply(macro, ApplyScope.Arrangement, new ToolWindowRegistry());

        Assert.Same(current.DockArea, result.State.DockArea);
        Assert.Equal(RightPrimary, Get(result.State, "a").Slot);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void TW_10_7_E23_window_registered_after_the_macro_is_untouched()
    {
        var registry = Registry(new ToolWindowDescriptor("z", "Z", RightPrimary));
        var current = Layout(
            Window("a", LeftPrimary, 0),
            Window("z", RightPrimary, 0) with { IsOpen = true, PairRatio = 0.7 });
        var macro = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true });

        var result = current.Apply(macro, ApplyScope.Arrangement, registry);

        var z = Get(result.State, "z");
        Assert.True(z.IsOpen);
        Assert.Equal(RightPrimary, z.Slot);
        Assert.Equal(0.7, z.PairRatio);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void TW_10_7_E24_macro_opening_evicts_the_unmentioned_without_fixes()
    {
        var current = Layout(
            Window("x", LeftPrimary, 0),
            Window("y", LeftPrimary, 1) with { IsOpen = true });
        var macro = Layout(Window("x", LeftPrimary, 0) with { IsOpen = true });

        var result = current.Apply(macro, ApplyScope.Arrangement, new ToolWindowRegistry());

        Assert.True(Get(result.State, "x").IsOpen);
        Assert.False(Get(result.State, "y").IsOpen);
        Assert.Empty(result.Fixes); // вытеснение TW-5.1 — поведение, не починка
    }

    [Fact]
    public void TW_10_7_E25_slot_order_is_mentioned_first_then_unmentioned()
    {
        var current = Layout(
            Window("a", LeftPrimary, 0),
            Window("z", LeftPrimary, 1) with { PairRatio = 0.7 },
            Window("b", LeftPrimary, 2));
        var macro = Layout(Window("b", LeftPrimary, 0), Window("a", LeftPrimary, 1));

        var result = current.Apply(macro, ApplyScope.Arrangement, new ToolWindowRegistry());

        Assert.Equal(0, Get(result.State, "b").Order);
        Assert.Equal(1, Get(result.State, "a").Order);
        Assert.Equal(2, Get(result.State, "z").Order);
        Assert.Equal(0.7, Get(result.State, "z").PairRatio); // атрибуты Z не переписаны
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void TW_10_7_mentioned_window_keeps_its_own_geometry()
    {
        var bounds = new FloatingBounds(1, 2, 300, 200);
        var current = Layout(Window("a", LeftPrimary, 0) with
        {
            PairRatio = 0.7,
            UndockWeight = 0.4,
            FloatingBounds = bounds,
        });
        var macro = Layout(Window("a", RightPrimary, 0) with
        {
            IsOpen = true,
            Mode = ToolWindowMode.DockUnpinned,
            LastInternalMode = ToolWindowMode.DockUnpinned,
            PairRatio = 0.9,
            UndockWeight = 0.9,
            FloatingBounds = new FloatingBounds(9, 9, 9, 9),
        });

        var result = current.Apply(macro, ApplyScope.Arrangement, new ToolWindowRegistry());

        var a = Get(result.State, "a");
        Assert.Equal(RightPrimary, a.Slot); // размещение — из макета
        Assert.Equal(ToolWindowMode.DockUnpinned, a.Mode);
        Assert.True(a.IsOpen);
        Assert.Equal(0.7, a.PairRatio); // геометрия панели — текущая
        Assert.Equal(0.4, a.UndockWeight);
        Assert.Equal(bounds, a.FloatingBounds);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void TW_10_7_new_sleeping_record_comes_from_the_macro_with_validated_bounds()
    {
        var replacement = new FloatingBounds(10, 10, 200, 100);
        var macro = Layout(Window("s", LeftPrimary, 0) with
        {
            PairRatio = 0.6,
            FloatingBounds = new FloatingBounds(5000, 0, 10, 10),
        });

        var result = LayoutState.Empty.Apply(
            macro, ApplyScope.Arrangement, new ToolWindowRegistry(),
            bounds => bounds.X > 1000 ? replacement : null);

        var sleeping = Get(result.State, "s");
        Assert.Equal(0.6, sleeping.PairRatio);
        Assert.Equal(replacement, sleeping.FloatingBounds);
        Assert.Equal(["TW-7.4"], FixRules(result));
    }

    [Fact]
    public void TW_10_7_existing_bounds_are_not_validated_by_arrangement()
    {
        var current = Layout(Window("a", LeftPrimary, 0) with
        {
            FloatingBounds = new FloatingBounds(5000, 0, 10, 10),
        });
        var macro = Layout(Window("a", LeftPrimary, 0));
        var calls = 0;

        var result = current.Apply(
            macro, ApplyScope.Arrangement, new ToolWindowRegistry(),
            _ => { calls++; return null; });

        Assert.Equal(0, calls); // живые bounds существующей панели валидатор не проходит
        Assert.Equal(new FloatingBounds(5000, 0, 10, 10), Get(result.State, "a").FloatingBounds);
    }

    [Fact]
    public void TW_10_7_active_tool_window_resets_without_a_fix()
    {
        var current = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true })
            with { ActiveToolWindowId = "a" };
        var macro = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true });

        var result = current.Apply(macro, ApplyScope.Arrangement, new ToolWindowRegistry());

        Assert.Null(result.State.ActiveToolWindowId);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void TW_10_6_sides_and_quick_access_come_from_the_macro()
    {
        var macro = LayoutState.Empty with
        {
            Left = new SideState(0.4, 0.6),
            QuickAccessSide = QuickAccessSide.Right,
        };

        var result = LayoutState.Empty.Apply(macro, ApplyScope.Arrangement, new ToolWindowRegistry());

        Assert.Equal(new SideState(0.4, 0.6), result.State.Left);
        Assert.Equal(QuickAccessSide.Right, result.State.QuickAccessSide);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void TW_10_4_macro_internal_conflict_is_repaired_with_a_report()
    {
        var macro = Layout(
            Window("x", LeftPrimary, 0) with { IsOpen = true },
            Window("y", LeftPrimary, 1) with { IsOpen = true });

        var result = LayoutState.Empty.Apply(macro, ApplyScope.Arrangement, new ToolWindowRegistry());

        Assert.True(Get(result.State, "x").IsOpen);
        Assert.False(Get(result.State, "y").IsOpen);
        Assert.Equal(["INV-2"], FixRules(result));
    }

    [Fact]
    public void TW_10_4_duplicate_mention_in_the_macro_keeps_the_first()
    {
        var macro = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("a", RightPrimary, 0));

        var result = LayoutState.Empty.Apply(macro, ApplyScope.Arrangement, new ToolWindowRegistry());

        var survivor = Assert.Single(result.State.ToolWindows);
        Assert.Equal(LeftPrimary, survivor.Slot);
        Assert.True(survivor.IsOpen);
        Assert.Equal(["INV-1"], FixRules(result));
    }

    // ---- TW-5.14 ResetToDefaults ----

    [Fact]
    public void TW_5_14_reset_to_defaults_builds_from_the_descriptors()
    {
        var registry = Registry(
            new ToolWindowDescriptor("a", "A", LeftPrimary) { DefaultOrder = 1 },
            new ToolWindowDescriptor("b", "B", LeftPrimary) { DefaultOrder = 0 },
            new ToolWindowDescriptor("c", "C", RightPrimary),
            new ToolWindowDescriptor("d", "D", RightPrimary)
            {
                DefaultMode = ToolWindowMode.Float,
                DefaultPairRatio = 0.6,
            });

        var state = LayoutApply.ResetToDefaults(registry);

        Assert.Equal(0, Get(state, "b").Order); // явный DefaultOrder — раньше
        Assert.Equal(1, Get(state, "a").Order);
        Assert.Equal(0, Get(state, "c").Order); // без DefaultOrder — порядок регистрации
        Assert.Equal(1, Get(state, "d").Order);
        Assert.All(state.ToolWindows, w => Assert.False(w.IsOpen));
        Assert.Equal(ToolWindowMode.Float, Get(state, "d").Mode);
        Assert.Equal(ToolWindowMode.DockPinned, Get(state, "d").LastInternalMode);
        Assert.Equal(0.6, Get(state, "d").PairRatio);
        Assert.Empty(LayoutInvariants.Validate(state, registry));
    }

    [Fact]
    public void TW_5_14_reset_via_arrangement_keeps_the_documents()
    {
        // Рецепт «сбросить размещение, не закрывая документы» (XML-doc ResetToDefaults).
        var registry = Registry(new ToolWindowDescriptor("a", "A", LeftPrimary));
        var current = (Layout(Window("a", RightPrimary, 0) with { IsOpen = true })
                with { QuickAccessSide = QuickAccessSide.Right })
            .OpenDocument("doc", EmptyRegistry);

        var result = current.Apply(LayoutApply.ResetToDefaults(registry), ApplyScope.Arrangement, registry);

        Assert.Same(current.DockArea, result.State.DockArea);
        Assert.Equal(LeftPrimary, Get(result.State, "a").Slot);
        Assert.False(Get(result.State, "a").IsOpen);
        Assert.Equal(QuickAccessSide.Left, result.State.QuickAccessSide);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void TW_9_10_E26_macro_closing_the_owner_keeps_its_dock_tab()
    {
        var registry = Registry(new ToolWindowDescriptor("t", "T", LeftPrimary)
        {
            TabFactory = new StubTabFactory("t:"),
        });
        var current = Layout(Window("t", LeftPrimary, 0) with { IsOpen = true }) with
        {
            DockArea = new DockAreaState
            {
                Root = new TabGroupNode { Tabs = ["t:1"], ActiveTabId = "t:1" },
                CurrentTabId = "t:1",
            },
        };
        var macro = Layout(Window("t", LeftPrimary, 0)); // макет закрывает T

        var result = current.Apply(macro, ApplyScope.Arrangement, registry);

        Assert.False(Get(result.State, "t").IsOpen);
        Assert.Same(current.DockArea, result.State.DockArea); // вкладка t:1 живёт дальше (TW-9.10)
        Assert.Empty(result.Fixes);
        Assert.Empty(LayoutInvariants.Validate(result.State, registry));
    }

    // ---- guards ----

    [Fact]
    public void Apply_validates_its_arguments()
    {
        var registry = new ToolWindowRegistry();

        Assert.Throws<ArgumentNullException>(
            () => LayoutState.Empty.Apply(null!, ApplyScope.Full, registry));
        Assert.Throws<ArgumentNullException>(
            () => LayoutState.Empty.Apply(LayoutState.Empty, ApplyScope.Full, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LayoutState.Empty.Apply(LayoutState.Empty, (ApplyScope)42, registry));
    }
}
