using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Berth.Controls;

/// <summary>
/// Stripe icon of one tool window — a templated control: the default template is a
/// <c>StripeIconFace</c> showing the application-supplied icon resource when
/// <see cref="ToolWindowDescriptor.IconKey"/> resolves to an <see cref="IImage"/>, otherwise
/// the initials of the title; the tooltip is the title, extended with the shortcut hint when
/// the workspace has a <see cref="BerthWorkspace.ShortcutHintProvider"/>. An open window
/// carries the <c>:open</c> pseudo-class, and the open highlight rides on
/// <see cref="TemplatedControl.Background"/> as a token binding at Template priority — a
/// pseudo-class style of the application (<c>StripeButton:open</c>) overrides it, and a
/// custom ControlTheme keyed by this type replaces the template; the default template
/// surfaces the value via TemplateBinding (docs/styling.md). <see cref="Title"/> and
/// <see cref="IconKey"/> are the read-only template anchors. A left click toggles openness
/// regardless of activity; the right-click context menu is the compact icon menu — both
/// reduce to core commands. The icon face is shared with the drag ghost of the slot gesture:
/// the user drags what they grabbed.
/// </summary>
[PseudoClasses(":open")]
public sealed class StripeButton : TemplatedControl
{
    /// <summary>Defines the read-only <see cref="Title"/> property.</summary>
    public static readonly DirectProperty<StripeButton, string> TitleProperty =
        AvaloniaProperty.RegisterDirect<StripeButton, string>(nameof(Title), o => o.Title);

    /// <summary>Defines the read-only <see cref="IconKey"/> property.</summary>
    public static readonly DirectProperty<StripeButton, string?> IconKeyProperty =
        AvaloniaProperty.RegisterDirect<StripeButton, string?>(nameof(IconKey), o => o.IconKey);

    private readonly BerthWorkspace _workspace;
    private bool _pressed;

    internal StripeButton(ToolWindowState window, ToolWindowDescriptor descriptor, BerthWorkspace workspace)
    {
        ToolWindowId = window.Id;
        IsOpen = window.IsOpen;
        Title = descriptor.Title;
        IconKey = descriptor.IconKey;
        _workspace = workspace;
        // The open highlight lives on the control's Background at Template priority: the
        // token stays the live default, an application pseudo-class style overrides it, and
        // the default template paints it on the icon face via TemplateBinding. The closed
        // face still needs a transparent brush — a null background defeats hit testing.
        if (window.IsOpen)
        {
            ThemeTokens.BindBrush(
                this, BackgroundProperty, BerthThemeKeys.OpenIcon, BerthBrushes.OpenIcon, BindingPriority.Template);
        }
        else
        {
            SetValue(BackgroundProperty, Brushes.Transparent, BindingPriority.Template);
        }

        var hint = workspace.ShortcutHintProvider?.Invoke(window.Id);
        ToolTip.SetTip(this, string.IsNullOrEmpty(hint) ? descriptor.Title : $"{descriptor.Title}  {hint}");
        PseudoClasses.Set(":open", window.IsOpen);
        ContextFlyout = ToolWindowMenus.BuildIconMenu(window, workspace);
    }

    /// <summary>Id of the tool window the icon represents.</summary>
    public string ToolWindowId { get; }

    /// <summary>Whether the represented window is open — open icons are highlighted (TW-6.4).</summary>
    public bool IsOpen { get; }

    /// <summary>Displayed title of the represented window — the initials source of the default icon face.</summary>
    public string Title { get; }

    /// <summary>Icon key of the registration, resolved against application resources (ADR-0003); null — the initials face.</summary>
    public string? IconKey { get; }

    /// <inheritdoc/>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // The no-include theme fallback (docs/styling.md): an application theme resource
        // wins through the implicit resolution; otherwise the built-in theme applies.
        BerthControlThemes.EnsureTheme(this);
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressed = true;
            // The press is also a drag candidate (TW-5.17): past the threshold the gesture
            // becomes a drag and this button never sees the release.
            _workspace.Drag?.Arm(new DragSubject(DragSourceKind.StripeIcon, ToolWindowId, Title), e);
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
/// The face of a stripe icon (TW-1.4): the application-supplied icon resource when the
/// icon key resolves to an <see cref="IImage"/>, otherwise the initials of the title. The
/// content of the default <see cref="StripeButton"/> template — fed by TemplateBinding —
/// and of the drag ghost of the slot gesture (TW-5.17, v0.26): the ghost shows the same
/// face the user grabbed.
/// </summary>
internal sealed class StripeIconFace : Border
{
    /// <summary>Defines the <see cref="IconKey"/> property.</summary>
    internal static readonly StyledProperty<string?> IconKeyProperty =
        AvaloniaProperty.Register<StripeIconFace, string?>(nameof(IconKey));

    /// <summary>Defines the <see cref="Title"/> property.</summary>
    internal static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<StripeIconFace, string>(nameof(Title), string.Empty);

    private readonly TextBlock _initials;

    public StripeIconFace()
    {
        ThemeTokens.BindSize(this, WidthProperty, BerthThemeKeys.StripeButtonSize, BerthMetrics.StripeButtonSize);
        ThemeTokens.BindSize(this, HeightProperty, BerthThemeKeys.StripeButtonSize, BerthMetrics.StripeButtonSize);
        CornerRadius = new CornerRadius(4);
        _initials = new TextBlock
        {
            // Seed from the default Title: setting Title to an empty string later raises no
            // property-changed (it equals the default), so an empty title must resolve to the
            // «?» fallback here, not stay null.
            Text = Initials(Title),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _initials;
    }

    /// <summary>The drag-ghost face (TW-5.17): built outside a template, fed directly.</summary>
    public StripeIconFace(string? iconKey, string title)
        : this()
    {
        IconKey = iconKey;
        Title = title;
    }

    /// <summary>Icon key resolved against application resources (ADR-0003); anything but an image keeps the initials.</summary>
    internal string? IconKey
    {
        get => GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    /// <summary>Title whose initials the fallback face shows.</summary>
    internal string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty)
        {
            _initials.Text = Initials(Title);
        }
        else if (change.Property == IconKeyProperty && ((ILogical)this).IsAttachedToLogicalTree)
        {
            ResolveIcon();
        }
    }

    /// <inheritdoc/>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // The icon key resolves against application resources (ADR-0003: the core stores
        // identifiers) under the current theme variant; anything but an image keeps the
        // initials fallback. Resolution is per-attach: a live theme switch repaints on the
        // next rebuild, which is outside the static skeleton's scope.
        ResolveIcon();
    }

    private void ResolveIcon()
    {
        if (IconKey is { } key
            && this.TryFindResource(key, ActualThemeVariant, out var resource)
            && resource is IImage image)
        {
            Child = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(5) };
        }
        else if (!ReferenceEquals(Child, _initials))
        {
            Child = _initials;
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
