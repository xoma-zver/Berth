using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Berth.Demo.Views;

public partial class MainView : UserControl
{
    // The demo keymap: activation shortcuts belong to the application, not to the
    // registrations (spec TW-5.5) — the workspace only executes the tri-state activation
    // and shows the hints its provider supplies (TW-6.4). Gestures are matched with
    // KeyGesture (canonical Key + modifier comparison) on the tunnel of KeyDown, so the
    // application keymap resolves Alt+digit before access-key or menu handling can claim it.
    private static readonly (string Id, KeyGesture Gesture, string Hint)[] Shortcuts =
    [
        ("project", new KeyGesture(Key.D1, KeyModifiers.Alt), "Alt+1"),
        ("structure", new KeyGesture(Key.D2, KeyModifiers.Alt), "Alt+2"),
        ("terminal", new KeyGesture(Key.D3, KeyModifiers.Alt), "Alt+3"),
        ("properties", new KeyGesture(Key.D4, KeyModifiers.Alt), "Alt+4"),
    ];

    public MainView()
    {
        InitializeComponent();
        Workspace.ShortcutHintProvider = id =>
        {
            foreach (var (candidate, _, hint) in Shortcuts)
            {
                if (string.Equals(candidate, id, System.StringComparison.Ordinal))
                {
                    return hint;
                }
            }

            return null;
        };
        // Tab titles belong to the application (DA-9.6): the layout stores ids only.
        Workspace.TabTitleProvider = id =>
            id.StartsWith("doc:", System.StringComparison.Ordinal) ? id[4..]
            : id.StartsWith("term:", System.StringComparison.Ordinal) ? id[5..]
            : null;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        foreach (var (id, gesture, _) in Shortcuts)
        {
            if (gesture.Matches(e))
            {
                Workspace.ActivateToolWindow(id);
                e.Handled = true;
                return;
            }
        }
    }
}
