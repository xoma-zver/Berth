using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Berth.Controls;

/// <summary>
/// Stripe icon of one tool window (spec TW-1.4): shows the application-supplied icon resource
/// when <see cref="ToolWindowDescriptor.IconKey"/> resolves to an <see cref="IImage"/>,
/// otherwise the initials of the title; the tooltip is the title, extended with the
/// application-supplied shortcut hint when the workspace has a
/// <see cref="BerthWorkspace.ShortcutHintProvider"/> (spec TW-5.5, TW-6.4). An open window is
/// highlighted and carries the <c>:open</c> pseudo-class (spec TW-6.4). A left click toggles
/// openness regardless of activity (spec TW-5.4); the right-click context menu is the compact
/// menu of TW-5.16 — both reduce to core commands (ADR-0004).
/// </summary>
public sealed class StripeButton : Decorator
{
    private readonly Border _face;
    private readonly string? _iconKey;
    private readonly BerthWorkspace _workspace;
    private bool _pressed;

    internal StripeButton(ToolWindowState window, ToolWindowDescriptor descriptor, BerthWorkspace workspace)
    {
        ToolWindowId = window.Id;
        IsOpen = window.IsOpen;
        _iconKey = descriptor.IconKey;
        _workspace = workspace;
        _face = new Border
        {
            Width = BerthMetrics.StripeButtonSize,
            Height = BerthMetrics.StripeButtonSize,
            Margin = new Thickness(4, 4, 4, 0),
            CornerRadius = new CornerRadius(4),
            Background = window.IsOpen ? BerthBrushes.OpenIcon : Brushes.Transparent,
            Child = new TextBlock
            {
                Text = Initials(descriptor.Title),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Child = _face;
        var hint = workspace.ShortcutHintProvider?.Invoke(window.Id);
        ToolTip.SetTip(this, string.IsNullOrEmpty(hint) ? descriptor.Title : $"{descriptor.Title}  {hint}");
        PseudoClasses.Set(":open", window.IsOpen);
        ContextFlyout = ToolWindowMenus.BuildIconMenu(window, workspace);
    }

    /// <summary>Id of the tool window the icon represents.</summary>
    public string ToolWindowId { get; }

    /// <summary>Whether the represented window is open — open icons are highlighted (spec TW-6.4).</summary>
    public bool IsOpen { get; }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressed = true;
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed && e.InitialPressMouseButton == MouseButton.Left)
        {
            // TW-5.4: the click toggles openness, regardless of the window's activity.
            // Openness is read from the command's input state, so the lambda does not
            // depend on the projection being rebuilt after every command.
            var id = ToolWindowId;
            _workspace.Execute(s => IsOpenIn(s, id) ? s.Close(id) : s.Open(id));
            e.Handled = true;
        }

        _pressed = false;
    }

    private static bool IsOpenIn(LayoutState state, string id) =>
        state.ToolWindows.Any(w => string.Equals(w.Id, id, StringComparison.Ordinal) && w.IsOpen);

    /// <inheritdoc/>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // The icon key resolves against application resources (ADR-0003: the core stores
        // identifiers) under the current theme variant; anything but an image keeps the
        // initials fallback. Resolution is per-attach: a live theme switch repaints on the
        // next rebuild, which is outside the static skeleton's scope.
        if (_iconKey is not null
            && this.TryFindResource(_iconKey, ActualThemeVariant, out var resource)
            && resource is IImage image)
        {
            _face.Child = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(5) };
        }
    }

    private static string Initials(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length switch
        {
            0 => "?",
            1 => FirstTextElements(words[0], 2),
            _ => FirstTextElements(words[0], 1) + FirstTextElements(words[1], 1),
        };
    }

    /// <summary>First text elements of a word — indexing by char would split surrogate pairs (an emoji in the title must not yield a broken glyph).</summary>
    private static string FirstTextElements(string word, int count)
    {
        var elements = StringInfo.GetTextElementEnumerator(word);
        var result = new StringBuilder();
        while (count-- > 0 && elements.MoveNext())
        {
            result.Append((string)elements.Current);
        }

        return result.ToString();
    }
}
