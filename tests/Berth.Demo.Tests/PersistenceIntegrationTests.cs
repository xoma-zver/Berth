using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Berth.Controls;
using Berth.Demo.ViewModels;
using Berth.Demo.Views;
using Xunit;

namespace Berth.Demo.Tests;

/// <summary>In-memory layout store standing in for the host's file/localStorage one.</summary>
internal sealed class MemoryLayoutStore : ILayoutStore
{
    public string? Json { get; set; }

    public string? Load() => Json;

    public void Save(string json) => Json = json;
}

/// <summary>
/// Integration tests of the application-side persistence over the real mini-IDE composition
/// (task 7.0): the demo view model with its registrations, factories and lifecycle wiring —
/// the end-to-end counterpart of the core round-trip property tests (TW-10.4).
/// </summary>
public sealed class PersistenceIntegrationTests
{
    [AvaloniaFact]
    public void Session_survives_restart_byte_for_byte()
    {
        var store = new MemoryLayoutStore();
        var first = new MainViewModel();
        first.AttachPersistence(store, validateBounds: null);

        // A session touching every persisted axis: a floating panel with bounds, a document
        // window, a side resize, a panel tab split and a closed panel.
        Mutate(first, s => s.SetMode("properties", ToolWindowMode.Float, new FloatingBounds(40, 40, 300, 200)));
        Mutate(first, s => s.Open("properties"));
        Mutate(first, s => s.MoveTabToNewWindow("doc:README.md", new FloatingBounds(60, 60, 400, 300)));
        Mutate(first, s => s.SetSideSize(ToolWindowSide.Left, 0.25));
        Mutate(first, s => s.SplitTab("term:Local (2)", SplitDirection.Right));
        Mutate(first, s => s.Close("project"));
        first.SaveLayout();
        var saved = store.Json;
        Assert.NotNull(saved);

        // «Restart»: a fresh composition restores from the same store.
        var second = new MainViewModel();
        second.AttachPersistence(store, validateBounds: null);

        Assert.Null(second.LastRestoreError);
        Assert.Empty(second.LastRestoreFixes); // a core-produced document needs no fixes (TW-10.4)
        Assert.Equal(saved, LayoutPersistence.Serialize(second.State!));
    }

    [AvaloniaFact]
    public void Corrupt_document_falls_back_to_the_default_composition()
    {
        var store = new MemoryLayoutStore { Json = "{ this is not json" };
        var vm = new MainViewModel();
        var defaults = LayoutPersistence.Serialize(vm.State!);

        vm.AttachPersistence(store, validateBounds: null);

        Assert.NotNull(vm.LastRestoreError);
        Assert.Equal(defaults, LayoutPersistence.Serialize(vm.State!));
    }

    [AvaloniaFact]
    public void Newer_schema_version_falls_back_to_the_default_composition()
    {
        // The explicit load error of TW-10.5: the migration chain arrives with the first
        // real SchemaVersion bump; until then any other version is rejected, not guessed at.
        var store = new MemoryLayoutStore { Json = """{ "schemaVersion": 999 }""" };
        var vm = new MainViewModel();
        var defaults = LayoutPersistence.Serialize(vm.State!);

        vm.AttachPersistence(store, validateBounds: null);

        Assert.NotNull(vm.LastRestoreError);
        Assert.Equal(defaults, LayoutPersistence.Serialize(vm.State!));
    }

    [AvaloniaFact]
    public void Reset_layout_restores_placement_but_keeps_documents()
    {
        var vm = new MainViewModel();
        vm.OpenFileDocument("extra.md");
        Mutate(vm, s => s.Move("project", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Secondary), 0));

        vm.ResetLayout();

        var state = vm.State!;
        // Placement is back at the descriptor defaults (Arrangement, TW-10.6)…
        var project = state.ToolWindows.First(w => w.Id == "project");
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary), project.Slot);
        Assert.All(state.ToolWindows, w => Assert.False(w.IsOpen)); // defaults are closed
        // …while the open documents and panel trees survive (E20).
        Assert.True(ContainsTab(state, "doc:extra.md"));
        Assert.True(ContainsTab(state, "doc:README.md"));
        Assert.True(ContainsTab(state, "term:Local"));
    }

    [AvaloniaFact]
    public void Sleeping_ids_survive_the_application_round_trip()
    {
        // A layout written by a fuller application: it contains a tool window this demo never
        // registers. The record must sleep through restore and be written back (TW-10.2).
        var seed = new MainViewModel();
        var withSleeping = seed.State! with
        {
            ToolWindows = seed.State!.ToolWindows.Add(new ToolWindowState(
                "mystery", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Secondary), 0)),
        };
        var store = new MemoryLayoutStore { Json = LayoutPersistence.Serialize(withSleeping) };

        var vm = new MainViewModel();
        vm.AttachPersistence(store, validateBounds: null);
        Assert.Null(vm.LastRestoreError);
        store.Json = null;
        vm.SaveLayout();

        Assert.Contains("mystery", store.Json, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Restored_layout_materializes_in_the_mini_ide()
    {
        // First session: an extra document and a floating panel.
        var store = new MemoryLayoutStore();
        var first = new MainViewModel();
        first.AttachPersistence(store, validateBounds: null);
        first.OpenFileDocument("extra.md");
        Mutate(first, s => s.SetMode("properties", ToolWindowMode.Float, new FloatingBounds(40, 40, 300, 200)));
        Mutate(first, s => s.Open("properties"));
        first.SaveLayout();

        // Second session: the real MainView materializes the restored layout.
        var vm = new MainViewModel();
        vm.AttachPersistence(store, validateBounds: null);
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        window.UpdateLayout();

        var tabs = window.GetVisualDescendants().OfType<DockTabHost>().Select(h => h.TabId).ToList();
        Assert.Contains("doc:extra.md", tabs);
        var workspace = window.GetVisualDescendants().OfType<BerthWorkspace>().Single();
        Assert.True(workspace.CanFloat); // the floating layer is live on this platform
        window.Close();
    }

    private static void Mutate(MainViewModel vm, Func<LayoutState, LayoutState> command)
    {
        // The application-transition contract: the application reports what it performs
        // itself, one call per command (NotifyTransition).
        var before = vm.State!;
        var after = command(before);
        vm.Lifecycle.NotifyTransition(before, after);
        vm.State = after;
    }

    /// <summary>Whether the tab id lives anywhere in the layout — the demo-side tree walk over the public node model.</summary>
    private static bool ContainsTab(LayoutState state, string tabId)
    {
        bool InTree(TabTreeNode node) => node switch
        {
            TabGroupNode group => group.Tabs.Contains(tabId, StringComparer.Ordinal),
            SplitNode split => split.Children.Any(c => InTree(c.Node)),
            _ => false,
        };

        return InTree(state.DockArea.Root)
            || state.DockArea.Windows.Any(w => InTree(w.Root))
            || state.ToolWindows.Any(p => InTree(p.ContentTree));
    }
}
