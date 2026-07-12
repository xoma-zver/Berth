using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Berth.Demo.ViewModels;

/// <summary>
/// Fake project listing demonstrating the MVVM content path: the tool window factory returns
/// this view model and the application's ViewLocator builds <c>ProjectView</c> for it.
/// </summary>
public partial class ProjectViewModel : ViewModelBase
{
    [ObservableProperty]
    private string? _selectedFile;

    /// <summary>
    /// Application callback opening the selected file as a document (spec DA-5.1); the
    /// composition root wires it to the command channel, the view invokes it on double-tap.
    /// </summary>
    public Action<string>? OpenFile { get; set; }

    public ObservableCollection<string> Files { get; } =
    [
        "Berth.slnx",
        "src/Berth.Core",
        "src/Berth.Avalonia",
        "docs/spec/tool-windows.md",
        "docs/spec/document-area.md",
        "README.md",
    ];
}
