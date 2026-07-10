using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Berth.Controls;

/// <summary>
/// Stripe icon of one tool window (spec TW-1.4): shows the application-supplied icon resource
/// when <see cref="ToolWindowDescriptor.IconKey"/> resolves to an <see cref="IImage"/>,
/// otherwise the initials of the title; the tooltip is the title. An open window is
/// highlighted and carries the <c>:open</c> pseudo-class (spec TW-6.4). The button is a
/// passive visual: input wiring reduces to core commands in a later task (ADR-0004).
/// </summary>
public sealed class StripeButton : Decorator
{
    private readonly Border _face;
    private readonly string? _iconKey;

    internal StripeButton(string toolWindowId, string title, string? iconKey, bool isOpen)
    {
        ToolWindowId = toolWindowId;
        IsOpen = isOpen;
        _iconKey = iconKey;
        _face = new Border
        {
            Width = BerthMetrics.StripeButtonSize,
            Height = BerthMetrics.StripeButtonSize,
            Margin = new Thickness(4, 4, 4, 0),
            CornerRadius = new CornerRadius(4),
            Background = isOpen ? BerthBrushes.OpenIcon : Brushes.Transparent,
            Child = new TextBlock
            {
                Text = Initials(title),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Child = _face;
        ToolTip.SetTip(this, title);
        PseudoClasses.Set(":open", isOpen);
    }

    /// <summary>Id of the tool window the icon represents.</summary>
    public string ToolWindowId { get; }

    /// <summary>Whether the represented window is open — open icons are highlighted (spec TW-6.4).</summary>
    public bool IsOpen { get; }

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
