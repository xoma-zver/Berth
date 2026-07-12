using CsCheck;
using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Tests of the fluent composition builder (task 7.1): registrations run through the live
/// registration path (spec TW-10.3, TW-9.2), the default arrangement follows the
/// ResetToDefaults rule (TW-5.14), initial openness is expressed by ordinary commands
/// (spec E15: openness is not a descriptor field), and every built composition is valid and
/// passes Apply without a single fix — the round-trip invariant of TW-10.4.
/// </summary>
public class CompositionBuilderTests
{
    /// <summary>Counting body factory: observes the Eager creation moment (spec TW-9.2).</summary>
    private sealed class CountingBodyFactory : IToolWindowContentFactory
    {
        public int Created { get; private set; }

        public object CreateContent(string toolWindowId)
        {
            Created++;
            return new object();
        }

        public void ReleaseContent(string toolWindowId, object content)
        {
        }
    }

    [Fact]
    public void Build_produces_registered_descriptors_and_seeds_bodies()
    {
        var composition = new LayoutCompositionBuilder()
            .AddToolWindow("project", "Project", w => w
                .Slot(ToolWindowSide.Left, ToolWindowGroup.Primary)
                .Content(_ => new object()))
            .AddToolWindow("bare", "Bare", w => w.Slot(ToolWindowSide.Right, ToolWindowGroup.Primary))
            .Build();

        Assert.True(composition.Registry.TryGet("project", out var descriptor));
        Assert.Equal("Project", descriptor!.Title);
        var project = composition.State.ToolWindows.Single(w => w.Id == "project");
        Assert.False(project.IsOpen); // defaults are closed; openness is a command (E15)
        // The body tab is seeded for a window with a body factory (TW-9.5)…
        var body = Assert.IsType<TabGroupNode>(project.ContentTree);
        Assert.Equal(["project"], body.Tabs);
        // …and not for one without.
        var bare = composition.State.ToolWindows.Single(w => w.Id == "bare");
        Assert.Equal(TabGroupNode.Empty, bare.ContentTree);
    }

    [Fact]
    public void TW_5_14_default_order_is_honoured_like_reset_to_defaults()
    {
        // Two windows share a slot; the explicit Order reverses the add order — the same rule
        // as ResetToDefaults (explicit DefaultOrder first, then registration order).
        var composition = new LayoutCompositionBuilder()
            .AddToolWindow("second", "Second", w => w.Slot(ToolWindowSide.Left, ToolWindowGroup.Primary).Order(1))
            .AddToolWindow("first", "First", w => w.Slot(ToolWindowSide.Left, ToolWindowGroup.Primary).Order(0))
            .Build();

        Assert.Equal(0, composition.State.ToolWindows.Single(w => w.Id == "first").Order);
        Assert.Equal(1, composition.State.ToolWindows.Single(w => w.Id == "second").Order);
        // Without open commands the built state IS the ResetToDefaults arrangement (TW-5.14).
        Assert.Equal(
            LayoutPersistence.Serialize(LayoutApply.ResetToDefaults(composition.Registry)),
            LayoutPersistence.Serialize(composition.State));
    }

    [Fact]
    public void Initial_commands_open_windows_documents_and_panel_tabs()
    {
        var composition = new LayoutCompositionBuilder()
            .AddToolWindow("project", "Project", w => w.Slot(ToolWindowSide.Left, ToolWindowGroup.Primary))
            .AddToolWindow("terminal", "Terminal", w => w
                .Slot(ToolWindowSide.Bottom, ToolWindowGroup.Primary)
                .Tabs(id => id.StartsWith("term:", StringComparison.Ordinal), _ => new object()))
            .AddDockContent(id => id.StartsWith("doc:", StringComparison.Ordinal), _ => new object())
            .Open("project")
            .Open("terminal")
            .OpenDocument("doc:readme")
            .OpenPanelTab("term:local")
            .Build();

        var state = composition.State;
        Assert.True(state.ToolWindows.Single(w => w.Id == "project").IsOpen);
        Assert.True(state.ToolWindows.Single(w => w.Id == "terminal").IsOpen);
        // The document opened after the tool windows: its activation cleared the active tool
        // window (TW-6.5, DA-6.2) — commands compose in call order, like a live session.
        Assert.Null(state.ActiveToolWindowId);
        Assert.Equal("doc:readme", state.DockArea.CurrentTabId);
        var terminalTree = Assert.IsType<TabGroupNode>(
            state.ToolWindows.Single(w => w.Id == "terminal").ContentTree);
        Assert.Contains("term:local", terminalTree.Tabs);
    }

    [Fact]
    public void Commands_may_be_chained_before_the_registrations_they_reference()
    {
        // Build is two-phase: registrations first, then commands in call order.
        var composition = new LayoutCompositionBuilder()
            .Open("late")
            .AddToolWindow("late", "Late", w => w.Slot(ToolWindowSide.Right, ToolWindowGroup.Secondary))
            .Build();

        Assert.True(composition.State.ToolWindows.Single(w => w.Id == "late").IsOpen);
    }

    [Fact]
    public void TW_9_2_eager_content_is_created_at_build_and_lazy_content_is_not()
    {
        var eager = new CountingBodyFactory();
        var lazy = new CountingBodyFactory();
        var composition = new LayoutCompositionBuilder()
            .AddToolWindow("eager", "Eager", w => w.Slot(ToolWindowSide.Left, ToolWindowGroup.Primary).Eager().Content(eager))
            .AddToolWindow("lazy", "Lazy", w => w.Slot(ToolWindowSide.Right, ToolWindowGroup.Primary).Content(lazy))
            .Build();

        Assert.Equal(1, eager.Created);
        Assert.Equal(0, lazy.Created);
        // The lazy body materializes on first pull, through the same coordinator.
        Assert.NotNull(composition.Lifecycle.GetOrCreateToolWindowContent("lazy"));
        Assert.Equal(1, lazy.Created);
    }

    [Fact]
    public void TW_9_11_delegate_tab_claims_confirm_ownership()
    {
        var composition = new LayoutCompositionBuilder()
            .AddToolWindow("terminal", "Terminal", w => w
                .Slot(ToolWindowSide.Bottom, ToolWindowGroup.Primary)
                .Tabs(id => id.StartsWith("term:", StringComparison.Ordinal), _ => new object()))
            .AddDockContent(id => id.StartsWith("doc:", StringComparison.Ordinal), _ => new object())
            .Build();

        Assert.Equal(TabOwner.ToolWindow("terminal"), composition.Registry.ResolveTabOwner("term:x"));
        Assert.Equal(TabOwner.DockArea, composition.Registry.ResolveTabOwner("doc:x"));
        Assert.Null(composition.Registry.ResolveTabOwner("unclaimed"));
    }

    [Fact]
    public void Delegate_release_is_invoked_by_the_lifecycle()
    {
        var released = 0;
        var composition = new LayoutCompositionBuilder()
            .AddToolWindow("panel", "Panel", w => w
                .Slot(ToolWindowSide.Left, ToolWindowGroup.Primary)
                .DisposeOnClose()
                .Content(_ => new object(), (_, _) => released++))
            .Open("panel")
            .Build();

        var state = composition.State;
        Assert.NotNull(composition.Lifecycle.GetOrCreateToolWindowContent("panel"));
        var closed = state.Close("panel");
        composition.Lifecycle.NotifyTransition(state, closed);
        Assert.Equal(1, released); // DisposeOnClose releases on the transition out of openness
    }

    [Fact]
    public void Duplicate_tool_window_id_fails_at_add()
    {
        var builder = new LayoutCompositionBuilder()
            .AddToolWindow("dup", "One", w => w.Slot(ToolWindowSide.Left, ToolWindowGroup.Primary));

        Assert.Throws<ArgumentException>(() =>
            builder.AddToolWindow("dup", "Two", w => w.Slot(ToolWindowSide.Right, ToolWindowGroup.Primary)));
    }

    [Fact]
    public void Build_is_single_use()
    {
        var builder = new LayoutCompositionBuilder();
        _ = builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Open_of_an_unknown_id_fails_at_build()
    {
        var builder = new LayoutCompositionBuilder().Open("ghost");

        Assert.Throws<ArgumentException>(() => builder.Build());
    }

    [Fact]
    public void OpenPanelTab_of_an_unclaimed_id_fails_at_build()
    {
        var builder = new LayoutCompositionBuilder()
            .AddToolWindow("panel", "Panel", w => w.Slot(ToolWindowSide.Left, ToolWindowGroup.Primary))
            .OpenPanelTab("unclaimed:tab");

        Assert.Throws<ArgumentException>(() => builder.Build());
    }

    // ---- the round-trip property of TW-10.4 over arbitrary compositions ----

    [Fact]
    public void TW_10_4_any_built_composition_is_valid_and_fix_free()
    {
        var genWindow =
            from slot in Gen.Int[0, ToolWindowSlot.All.Length - 1]
            from order in Gen.Int[-1, 3]
            from mode in Gen.Int[0, 4]
            from eager in Gen.Bool
            from dispose in Gen.Bool
            from body in Gen.Bool
            from tabs in Gen.Bool
            from open in Gen.Bool
            select (slot, order, mode, eager, dispose, body, tabs, open);
        var scenario =
            from windows in genWindow.Array[0, 5]
            from dockClaim in Gen.Bool
            from documents in Gen.Int[0, 5].Array[0, 3]
            from panelTabs in Gen.Int[0, 15].Array[0, 4]
            select (windows, dockClaim, documents, panelTabs);

        scenario.Sample(
            s =>
            {
                var builder = new LayoutCompositionBuilder();
                for (var i = 0; i < s.windows.Length; i++)
                {
                    var (slot, order, mode, eager, dispose, body, tabs, _) = s.windows[i];
                    var prefix = $"tw{i}:";
                    builder.AddToolWindow($"tw{i}", $"Window {i}", w =>
                    {
                        w.Slot(ToolWindowSlot.All[slot].Side, ToolWindowSlot.All[slot].Group)
                            .Mode((ToolWindowMode)mode);
                        if (order >= 0)
                        {
                            w.Order(order);
                        }

                        if (eager)
                        {
                            w.Eager();
                        }

                        if (dispose)
                        {
                            w.DisposeOnClose();
                        }

                        if (body)
                        {
                            w.Content(_ => new object());
                        }

                        if (tabs)
                        {
                            w.Tabs(id => id.StartsWith(prefix, StringComparison.Ordinal), _ => new object());
                        }
                    });
                }

                if (s.dockClaim)
                {
                    builder.AddDockContent(
                        id => id.StartsWith("doc:", StringComparison.Ordinal), _ => new object());
                }

                for (var i = 0; i < s.windows.Length; i++)
                {
                    if (s.windows[i].open)
                    {
                        builder.Open($"tw{i}");
                    }
                }

                foreach (var seed in s.documents)
                {
                    builder.OpenDocument($"doc:{seed}");
                }

                var claimed = Enumerable.Range(0, s.windows.Length).Where(i => s.windows[i].tabs).ToList();
                foreach (var seed in s.panelTabs)
                {
                    if (claimed.Count > 0)
                    {
                        builder.OpenPanelTab($"tw{claimed[seed % claimed.Count]}:t{seed % 3}");
                    }
                }

                var composition = builder.Build();
                Assert.Empty(LayoutInvariants.Validate(composition.State, composition.Registry));
                var result = composition.State.Apply(composition.State, ApplyScope.Full, composition.Registry);
                Assert.Empty(result.Fixes);
                Assert.Equal(
                    LayoutPersistence.Serialize(composition.State),
                    LayoutPersistence.Serialize(result.State));
            },
            iter: 1_000);
    }
}
