using CsCheck;
using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Property test for spec section 4 and section 5: any sequence of core operations, applied to a
/// valid starting layout, keeps INV-1…INV-7 (and never throws for a known id).
/// </summary>
public class InvariantPropertyTests
{
    [Fact]
    public void Any_sequence_of_operations_preserves_invariants()
    {
        var scenario =
            from windowCount in Gen.Int[1, 8]
            from slotIndices in Gen.Int[0, ToolWindowSlot.All.Length - 1].Array[windowCount]
            from opCount in Gen.Int[0, 40]
            from ops in GenOp(windowCount).Array[opCount]
            select (slotIndices, ops);

        scenario.Sample(
            x =>
            {
                var (registry, state) = BuildStart(x.slotIndices);
                foreach (var op in x.ops)
                {
                    state = op.Apply(state);
                    Assert.Empty(LayoutInvariants.Validate(state, registry));
                }
            },
            iter: 100_000);
    }

    /// <summary>Builds a trivially valid starting layout: every window closed, DockPinned, dense per-slot order.</summary>
    private static (ToolWindowRegistry Registry, LayoutState State) BuildStart(int[] slotIndices)
    {
        var registry = new ToolWindowRegistry();
        var perSlotCount = new Dictionary<ToolWindowSlot, int>();
        var windows = new List<ToolWindowState>(slotIndices.Length);
        for (var i = 0; i < slotIndices.Length; i++)
        {
            var slot = ToolWindowSlot.All[slotIndices[i]];
            var order = perSlotCount.GetValueOrDefault(slot);
            perSlotCount[slot] = order + 1;
            var id = $"tw{i}";
            registry.Register(new ToolWindowDescriptor(id, id, slot));
            windows.Add(new ToolWindowState(id, slot, order));
        }

        return (registry, LayoutState.Empty with { ToolWindows = [.. windows] });
    }

    /// <summary>Generates one random command over the window ids <c>tw0…tw{n-1}</c>.</summary>
    private static Gen<Op> GenOp(int n)
    {
        var genId = from i in Gen.Int[0, n - 1] select $"tw{i}";
        var genSlot = from i in Gen.Int[0, ToolWindowSlot.All.Length - 1] select ToolWindowSlot.All[i];
        var genMode = Gen.OneOfConst(
            ToolWindowMode.DockPinned, ToolWindowMode.DockUnpinned, ToolWindowMode.Undock,
            ToolWindowMode.Float, ToolWindowMode.Window);
        var genBounds =
            from present in Gen.Bool
            select present ? (FloatingBounds?)new FloatingBounds(10, 20, 300, 200) : null;
        var genQuickAccess = Gen.OneOfConst(QuickAccessSide.Left, QuickAccessSide.Right);
        var genSide = Gen.OneOfConst(ToolWindowSide.Left, ToolWindowSide.Right, ToolWindowSide.Bottom);
        var genFraction = Gen.Double[0.05, 0.95]; // valid fractions: strictly within the (0..1) invariant (INV-4)

        return Gen.OneOf(
            from id in genId from activate in Gen.Bool select (Op)new OpenOp(id, activate),
            from id in genId select (Op)new CloseOp(id),
            Gen.Const((Op)new ActivateDocumentOp()),
            from id in genId from mode in genMode from bounds in genBounds select (Op)new SetModeOp(id, mode, bounds),
            from id in genId from slot in genSlot from index in Gen.Int[0, n + 1] select (Op)new MoveOp(id, slot, index),
            from id in genId from visible in Gen.Bool select (Op)new SetIconVisibleOp(id, visible),
            Gen.Const((Op)new HideAllOp()),
            from side in genQuickAccess select (Op)new SetQuickAccessOp(side),
            from side in genSide from weight in genFraction select (Op)new SetSideSizeOp(side, weight),
            from side in genSide from share in genFraction select (Op)new SetSideRatioOp(side, share),
            from id in genId from weight in genFraction select (Op)new SetUndockWeightOp(id, weight),
            from id in genId select (Op)new SetFloatingBoundsOp(id));
    }

    private abstract record Op
    {
        public abstract LayoutState Apply(LayoutState state);
    }

    private sealed record OpenOp(string Id, bool Activate) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.Open(Id, Activate);
    }

    private sealed record CloseOp(string Id) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.Close(Id);
    }

    private sealed record ActivateDocumentOp : Op
    {
        public override LayoutState Apply(LayoutState state) => state.ActivateDocument();
    }

    private sealed record SetModeOp(string Id, ToolWindowMode Mode, FloatingBounds? Bounds) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.SetMode(Id, Mode, Bounds);
    }

    private sealed record MoveOp(string Id, ToolWindowSlot Slot, int Index) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.Move(Id, Slot, Index);
    }

    private sealed record SetIconVisibleOp(string Id, bool Visible) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.SetIconVisible(Id, Visible);
    }

    private sealed record HideAllOp : Op
    {
        public override LayoutState Apply(LayoutState state) => state.HideAll();
    }

    private sealed record SetQuickAccessOp(QuickAccessSide Side) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.SetQuickAccessSide(Side);
    }

    private sealed record SetSideSizeOp(ToolWindowSide Side, double Weight) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.SetSideSize(Side, Weight);
    }

    private sealed record SetSideRatioOp(ToolWindowSide Side, double PrimaryShare) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.SetSideRatio(Side, PrimaryShare);
    }

    private sealed record SetUndockWeightOp(string Id, double Weight) : Op
    {
        public override LayoutState Apply(LayoutState state) => state.SetUndockWeight(Id, Weight);
    }

    private sealed record SetFloatingBoundsOp(string Id) : Op
    {
        public override LayoutState Apply(LayoutState state) =>
            state.SetFloatingBounds(Id, new FloatingBounds(10, 20, 300, 200));
    }
}
