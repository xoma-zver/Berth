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
