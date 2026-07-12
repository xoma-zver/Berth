using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Berth;
using Xunit;

namespace Berth.Controls.Tests;

/// <summary>
/// Tests of the markup-declared composition (task 7.1): <see cref="BerthLayoutDefinition"/>
/// translates into the fluent builder, templates bridge into content factories with honest
/// lifecycle semantics (spec TW-9.2), direct markup content is the Keep-only singleton, and
/// <see cref="BerthWorkspace.Definition"/> self-assembles a zero-code workspace while explicit
/// properties win.
/// </summary>
public class LayoutDefinitionTests
{
    private static ToolWindowDefinition Window(string id, Action<ToolWindowDefinition>? configure = null)
    {
        var definition = new ToolWindowDefinition { Id = id, Title = id };
        configure?.Invoke(definition);
        return definition;
    }

    private static (Window Window, BerthWorkspace Workspace) ShowDefinition(BerthLayoutDefinition definition)
    {
        var workspace = new BerthWorkspace { Definition = definition };
        var window = new Window { Width = 800, Height = 600, Content = workspace };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, workspace);
    }

    [AvaloniaFact]
    public void Definition_builds_the_same_composition_as_the_builder()
    {
        var definition = new BerthLayoutDefinition();
        definition.Items.Add(Window("project", w =>
        {
            w.Side = ToolWindowSide.Left;
            w.Group = ToolWindowGroup.Primary;
            w.IsOpen = true;
            w.ContentTemplate = new FuncDataTemplate<object>((_, _) => new TextBlock());
        }));
        definition.Items.Add(Window("terminal", w =>
        {
            w.Side = ToolWindowSide.Bottom;
            w.TabIdPrefix = "term:";
            w.TabTemplate = new FuncDataTemplate<object>((_, _) => new TextBox());
        }));
        definition.Items.Add(new DockContentDefinition
        {
            TabIdPrefix = "doc:",
            ContentTemplate = new FuncDataTemplate<object>((_, _) => new TextBox()),
        });
        definition.Items.Add(new DocumentDefinition { Id = "doc:readme" });
        definition.Items.Add(new PanelTabDefinition { Id = "term:local" });

        var composition = definition.Build();

        var state = composition.State;
        Assert.True(state.ToolWindows.Single(w => w.Id == "project").IsOpen);
        Assert.False(state.ToolWindows.Single(w => w.Id == "terminal").IsOpen);
        Assert.Equal("doc:readme", state.DockArea.CurrentTabId);
        var terminalTree = Assert.IsType<TabGroupNode>(
            state.ToolWindows.Single(w => w.Id == "terminal").ContentTree);
        Assert.Contains("term:local", terminalTree.Tabs);
        Assert.Equal(TabOwner.DockArea, composition.Registry.ResolveTabOwner("doc:x"));
        Assert.Equal(TabOwner.ToolWindow("terminal"), composition.Registry.ResolveTabOwner("term:x"));
        // Each Build produces a fresh, independent composition.
        Assert.NotSame(composition.Registry, definition.Build().Registry);
    }

    [AvaloniaFact]
    public void Workspace_self_assembles_from_a_definition()
    {
        var body = new TextBlock { Text = "direct" };
        var definition = new BerthLayoutDefinition();
        definition.Items.Add(Window("project", w =>
        {
            w.IsOpen = true;
            w.Content = body;
        }));

        var (window, workspace) = ShowDefinition(definition);

        Assert.NotNull(workspace.Registry);
        Assert.NotNull(workspace.Lifecycle);
        Assert.NotNull(workspace.State);
        var decorator = WorkspaceTestSupport.Decorator(window, "project");
        Assert.NotNull(decorator);
        // The direct markup content is hosted as-is (the Control path of TW-9.3).
        Assert.Same(body, WorkspaceTestSupport.TabHost(window, "project").Child);
        window.Close();
    }

    [AvaloniaFact]
    public void Explicit_registry_wins_over_a_definition()
    {
        var registry = new ToolWindowRegistry();
        var definition = new BerthLayoutDefinition();
        definition.Items.Add(Window("ghost"));
        var workspace = new BerthWorkspace { Registry = registry, Definition = definition };
        var window = new Window { Width = 400, Height = 300, Content = workspace };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(registry, workspace.Registry);
        Assert.Null(workspace.Lifecycle);
        Assert.Null(workspace.State); // the definition was ignored wholesale
        window.Close();
    }

    [AvaloniaFact]
    public void Direct_content_under_dispose_on_close_is_rejected()
    {
        var definition = new BerthLayoutDefinition();
        definition.Items.Add(Window("t", w =>
        {
            w.RetentionPolicy = ContentRetentionPolicy.DisposeOnClose;
            w.Content = new TextBlock();
        }));

        // The markup singleton cannot be recreated after a release (TW-9.2) — fail fast.
        Assert.Throws<InvalidOperationException>(definition.Build);
    }

    [AvaloniaFact]
    public void Misconfigured_items_are_rejected()
    {
        // A lone tab claim: the prefix without the template (and vice versa) creates tabs
        // nothing can materialize (TW-9.11).
        var loneClaim = new BerthLayoutDefinition();
        loneClaim.Items.Add(Window("t", w => w.TabIdPrefix = "t:"));
        Assert.Throws<InvalidOperationException>(loneClaim.Build);

        var bothContents = new BerthLayoutDefinition();
        bothContents.Items.Add(Window("t", w =>
        {
            w.Content = new TextBlock();
            w.ContentTemplate = new FuncDataTemplate<object>((_, _) => new TextBlock());
        }));
        Assert.Throws<InvalidOperationException>(bothContents.Build);

        var dockWithoutTemplate = new BerthLayoutDefinition();
        dockWithoutTemplate.Items.Add(new DockContentDefinition { TabIdPrefix = "doc:" });
        Assert.Throws<InvalidOperationException>(dockWithoutTemplate.Build);

        var documentWithoutId = new BerthLayoutDefinition();
        documentWithoutId.Items.Add(new DocumentDefinition());
        Assert.Throws<InvalidOperationException>(documentWithoutId.Build);
    }

    [AvaloniaFact]
    public void TW_9_2_template_content_is_recreated_per_open_cycle()
    {
        var built = 0;
        var definition = new BerthLayoutDefinition();
        definition.Items.Add(Window("t", w =>
        {
            w.IsOpen = true;
            w.RetentionPolicy = ContentRetentionPolicy.DisposeOnClose;
            w.ContentTemplate = new FuncDataTemplate<object>((_, _) =>
            {
                built++;
                return new TextBox();
            });
        }));

        var (window, _) = ShowDefinition(definition);
        Assert.Equal(1, built);

        // The close releases (DisposeOnClose, TW-9.2); the reopen pulls a fresh build.
        var icon = WorkspaceTestSupport.Button(window, "t");
        WorkspaceTestSupport.Click(window, icon);
        WorkspaceTestSupport.Click(window, WorkspaceTestSupport.Button(window, "t"));
        Assert.Equal(2, built);
        window.Close();
    }

    [AvaloniaFact]
    public void TW_9_2_direct_content_is_the_single_keep_instance()
    {
        var body = new TextBox { Text = "keep" };
        var definition = new BerthLayoutDefinition();
        definition.Items.Add(Window("t", w =>
        {
            w.IsOpen = true;
            w.Content = body;
        }));

        var (window, _) = ShowDefinition(definition);
        Assert.Same(body, WorkspaceTestSupport.TabHost(window, "t").Child);

        // Close and reopen: KeepWhileRegistered retains the singleton and its view (TW-9.13).
        WorkspaceTestSupport.Click(window, WorkspaceTestSupport.Button(window, "t"));
        WorkspaceTestSupport.Click(window, WorkspaceTestSupport.Button(window, "t"));
        Assert.Same(body, WorkspaceTestSupport.TabHost(window, "t").Child);
        window.Close();
    }

    [AvaloniaFact]
    public void Xaml_markup_parses_into_a_working_definition()
    {
        const string xaml = """
            <berth:BerthLayoutDefinition xmlns="https://github.com/avaloniaui"
                                         xmlns:berth="using:Berth.Controls">
              <berth:ToolWindowDefinition Id="project" Title="Project"
                                          Side="Left" Group="Primary" IsOpen="True">
                <TextBlock Text="direct"/>
              </berth:ToolWindowDefinition>
              <berth:ToolWindowDefinition Id="terminal" Title="Terminal"
                                          Side="Bottom" TabIdPrefix="term:">
                <berth:ToolWindowDefinition.TabTemplate>
                  <DataTemplate>
                    <TextBox/>
                  </DataTemplate>
                </berth:ToolWindowDefinition.TabTemplate>
              </berth:ToolWindowDefinition>
              <berth:DockContentDefinition TabIdPrefix="doc:">
                <DataTemplate>
                  <TextBox/>
                </DataTemplate>
              </berth:DockContentDefinition>
              <berth:DocumentDefinition Id="doc:readme"/>
              <berth:PanelTabDefinition Id="term:local"/>
            </berth:BerthLayoutDefinition>
            """;

        var definition = Assert.IsType<BerthLayoutDefinition>(AvaloniaRuntimeXamlLoader.Load(xaml));
        Assert.Equal(5, definition.Items.Count);
        var composition = definition.Build();

        var state = composition.State;
        Assert.True(state.ToolWindows.Single(w => w.Id == "project").IsOpen);
        Assert.Equal("doc:readme", state.DockArea.CurrentTabId);
        var project = state.ToolWindows.Single(w => w.Id == "project");
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary), project.Slot);
        // The parsed direct content is a live control instance.
        var direct = Assert.IsType<ToolWindowDefinition>(definition.Items[0]);
        Assert.IsType<TextBlock>(direct.Content);
        // The parsed tab template materializes claimed tabs.
        Assert.NotNull(composition.Lifecycle.MaterializeTab(state, "term:local").Content);
    }
}
