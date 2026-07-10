using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Berth.Demo.ViewModels;

/// <summary>
/// Composition root of the walking skeleton: registers demo tool windows exercising both
/// content paths (a view model resolved by the ViewLocator, and factory-built controls) and
/// both lifecycle axes (Eager/OnFirstOpen creation, KeepWhileRegistered/DisposeOnClose
/// retention), then builds the initial layout. The workspace binds State two-way, so user
/// gestures flow back into this property.
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

        // Application-performed transitions are the application's to report, one call per
        // command (the NotifyTransition contract).
        var opened = state.Open("project");
        Lifecycle.NotifyTransition(state, opened);
        var withTerminal = opened.Open("terminal");
        Lifecycle.NotifyTransition(opened, withTerminal);
        State = withTerminal;
    }

    public ToolWindowRegistry Registry { get; }

    public ContentLifecycle Lifecycle { get; }
}
