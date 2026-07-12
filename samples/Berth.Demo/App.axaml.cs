using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Berth.Controls;
using Berth.Demo.ViewModels;
using Berth.Demo.Views;

namespace Berth.Demo;

public partial class App : Application
{
    /// <summary>
    /// Host-supplied layout store (spec TW-10.1; the file — or localStorage — is the
    /// application's concern): the desktop host injects a <see cref="FileLayoutStore"/>, the
    /// browser one — a localStorage store, both via AppBuilder.AfterSetup. Null — no
    /// persistence, the demo starts from defaults (mobile hosts, headless tests).
    /// </summary>
    public ILayoutStore? LayoutStore { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            if (LayoutStore is { } store)
            {
                // Restore before the window shows; saved floating bounds are healed against
                // the window's screens (TW-7.4). The debounced autosave starts here, and the
                // closing write is the guaranteed snapshot of TW-7.5 — the state is saved
                // while every floating window is still open.
                viewModel.AttachPersistence(store, FloatingBoundsValidation.CreateValidator(window));
                window.Closing += (_, _) => viewModel.SaveLayout();
            }
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory =
                () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            var viewModel = new MainViewModel();
            var view = new MainView { DataContext = viewModel };
            singleViewPlatform.MainView = view;
            if (LayoutStore is { } store)
            {
                // The browser «screen» is the workspace (TW-7.7): the overlay validator heals
                // bounds against its area — including a layout carried over from the desktop
                // with screen coordinates. Before the first layout pass the workspace has no
                // size and bounds pass as saved; the render clamp keeps pseudo-windows
                // reachable regardless (TW-7.7). There is no closing event in the browser —
                // the debounced autosave is the only writer.
                viewModel.AttachPersistence(
                    store, FloatingBoundsValidation.CreateOverlayValidator(view.Workspace));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
