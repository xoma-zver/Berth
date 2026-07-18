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
/// menu of TW-5.16 — both reduce to core commands (ADR-0004). The icon face itself is a
/// <see cref="StripeIconFace"/>, shared with the drag ghost of the slot gesture (spec TW-5.17:
/// the user drags what they grabbed).
/// </summary>
public sealed class StripeButton : Decorator
{
    private readonly string _title;
    private readonly BerthWorkspace _workspace;
    private bool _pressed;

    internal StripeButton(ToolWindowState window, ToolWindowDescriptor descriptor, BerthWorkspace workspace)
    {
        ToolWindowId = window.Id;
        IsOpen = window.IsOpen;
        _title = descriptor.Title;
        _workspace = workspace;
        Child = new StripeIconFace(descriptor.IconKey, descriptor.Title)
        {
            Margin = new Thickness(4, 4, 4, 0),
            Background = window.IsOpen ? BerthBrushes.OpenIcon : Brushes.Transparent,
        };
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
            // The press is also a drag candidate (TW-5.17): past the threshold the gesture
            // becomes a drag and this button never sees the release.
            _workspace.Drag?.Arm(new DragSubject(DragSourceKind.StripeIcon, ToolWindowId, _title), e);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed
            && e.InitialPressMouseButton == MouseButton.Left
            && _workspace.Drag?.GestureConsumedClick != true)
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
}

/// <summary>
/// The face of a stripe icon (spec TW-1.4): the application-supplied icon resource when the
/// icon key resolves to an <see cref="IImage"/>, otherwise the initials of the title. Shared
/// between <see cref="StripeButton"/> and the drag ghost of the slot gesture (spec TW-5.17,
/// v0.26): the ghost shows the same face the user grabbed.
/// </summary>
internal sealed class StripeIconFace : Border
{
    private readonly string? _iconKey;

    public StripeIconFace(string? iconKey, string title)
    {
        _iconKey = iconKey;
        Width = BerthMetrics.StripeButtonSize;
        Height = BerthMetrics.StripeButtonSize;
        CornerRadius = new CornerRadius(4);
        Child = new TextBlock
        {
            Text = Initials(title),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

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
            Child = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(5) };
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
