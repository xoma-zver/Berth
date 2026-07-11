using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Berth.Demo.ViewModels;

/// <summary>
/// Composition root of the walking skeleton: registers demo tool windows exercising both
/// content paths (a view model resolved by the ViewLocator, and factory-built controls) and
/// both lifecycle axes (Eager/OnFirstOpen creation, KeepWhileRegistered/DisposeOnClose
/// retention), registers dock-area content claimed by the "doc:" prefix (spec TW-9.11), then
/// builds the initial layout with two open documents. The workspace binds State two-way, so
/// user gestures flow back into this property.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private LayoutState? _state;

    public MainViewModel()
    {
        Registry = new ToolWindowRegistry();
        Lifecycle = new ContentLifecycle(Registry);

        var state = Lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "project", "Project", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            IconKey = "ProjectIcon",
            CreationPolicy = ContentCreationPolicy.Eager,
            // The MVVM path: the factory returns a view model, the application's ViewLocator
            // (App.axaml data templates) builds the view.
            ContentFactory = new DelegateContentFactory(_ => new ProjectViewModel()),
        });
        state = Lifecycle.Register(state, new ToolWindowDescriptor(
            "structure", "Structure", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Secondary))
        {
            ContentFactory = new DelegateContentFactory(_ => new TextBlock
            {
                Text = "Structure of the selected file would appear here.",
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap,
            }),
        });
        state = Lifecycle.Register(state, new ToolWindowDescriptor(
            "terminal", "Terminal", new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Primary))
        {
            RetentionPolicy = ContentRetentionPolicy.DisposeOnClose,
            ContentFactory = new DelegateContentFactory(_ => new TextBox
            {
                Text = "$ echo DisposeOnClose — closing the panel forgets this text\n",
                AcceptsReturn = true,
                FontFamily = new FontFamily("monospace"),
            }),
        });
        state = Lifecycle.Register(state, new ToolWindowDescriptor(
            "properties", "Properties", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary))
        {
            ContentFactory = new DelegateContentFactory(_ => new TextBox
            {
                Text = "KeepWhileRegistered — this text survives close and reopen\n",
                AcceptsReturn = true,
            }),
        });

        // Dock-area documents: claimed by prefix (TW-9.11), materialized lazily by the
        // workspace (DA-9.3); titles come from the TabTitleProvider wired in MainView.
        state = Lifecycle.RegisterDockContent(state, new DelegateTabFactory(
            id => id.StartsWith("doc:", StringComparison.Ordinal),
            id => new TextBox
            {
                Text = $"// {id[4..]}\nOpen a second document, split and rotate via the tab menu.\n",
                AcceptsReturn = true,
                FontFamily = new FontFamily("monospace"),
            }));

        // Application-performed transitions are the application's to report, one call per
        // command (the NotifyTransition contract).
        var opened = state.Open("project");
        Lifecycle.NotifyTransition(state, opened);
        var withTerminal = opened.Open("terminal");
        Lifecycle.NotifyTransition(opened, withTerminal);
        var withReadme = withTerminal.OpenDocument("doc:README.md", Registry);
        Lifecycle.NotifyTransition(withTerminal, withReadme);
        var withSpec = withReadme.OpenDocument("doc:tool-windows.md", Registry);
        Lifecycle.NotifyTransition(withReadme, withSpec);
        State = withSpec;
    }

    public ToolWindowRegistry Registry { get; }

    public ContentLifecycle Lifecycle { get; }
}
