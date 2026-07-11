using Avalonia.Controls;
using Avalonia.Input;

namespace Berth.Demo.Views;

public partial class MainView : UserControl
{
    // The demo keymap: activation shortcuts belong to the application, not to the
    // registrations (spec TW-5.5) — the workspace only executes the tri-state activation
    // and shows the hints its provider supplies (TW-6.4).
    private static readonly (string Id, Key Key, string Hint)[] Shortcuts =
    [
        ("project", Key.D1, "Alt+1"),
        ("structure", Key.D2, "Alt+2"),
        ("terminal", Key.D3, "Alt+3"),
        ("properties", Key.D4, "Alt+4"),
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
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.Alt)
        {
            return;
        }

        foreach (var (id, key, _) in Shortcuts)
        {
            if (key == e.Key)
            {
                Workspace.ActivateToolWindow(id);
                e.Handled = true;
                return;
            }
        }
    }
}
