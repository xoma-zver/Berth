using Avalonia.Controls;
using Avalonia.Input;
using Berth.Demo.ViewModels;

namespace Berth.Demo.Views;

public partial class ProjectView : UserControl
{
    public ProjectView()
    {
        InitializeComponent();
        // Double-tapping a file opens it as a document through the command channel (DA-5.1);
        // the callback is wired by the composition root.
        Files.DoubleTapped += OnFileDoubleTapped;
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ProjectViewModel { SelectedFile: { } file } viewModel)
        {
            viewModel.OpenFile?.Invoke(file);
        }
    }
}
