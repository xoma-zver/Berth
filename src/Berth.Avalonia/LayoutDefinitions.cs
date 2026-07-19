using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace Berth.Controls;

/// <summary>
/// Markup-declarable default composition : the AXAML facade over
/// <see cref="LayoutCompositionBuilder"/>. Declared as a resource or inline, it lists tool
/// window registrations, dock content claims and the initially open documents and panel tabs;
/// <see cref="Build"/> translates the items into the fluent builder — all composition logic
/// lives there. Assign the definition to <see cref="BerthWorkspace.Definition"/> for a
/// zero-code workspace, or call <see cref="Build"/> yourself and wire the resulting
/// <see cref="LayoutComposition"/> like any code-built one. Each <see cref="Build"/> call
/// produces a fresh, independent composition. Configuration errors — a missing id, a lone
/// tab template, a markup singleton under DisposeOnClose — throw at <see cref="Build"/>.
/// </summary>
public sealed class BerthLayoutDefinition
{
    /// <summary>The declared items, in markup order; commands (openness) run after every registration.</summary>
    [Content]
    public IList<LayoutDefinitionItem> Items { get; } = [];

    /// <summary>Builds the composition through <see cref="LayoutCompositionBuilder"/>.</summary>
    /// <exception cref="InvalidOperationException">An item is misconfigured — see the item types for their requirements.</exception>
    public LayoutComposition Build()
    {
        var builder = new LayoutCompositionBuilder();
        foreach (var item in Items)
        {
            switch (item)
            {
                case ToolWindowDefinition window:
                    builder.AddToolWindow(BuildDescriptor(window));
                    break;
                case DockContentDefinition dock:
                    if (string.IsNullOrWhiteSpace(dock.TabIdPrefix) || dock.ContentTemplate is null)
                    {
                        throw new InvalidOperationException(
                            "A DockContentDefinition needs both TabIdPrefix and ContentTemplate: the prefix is the ownership claim, the template creates the content.");
                    }

                    builder.AddDockContent(new TemplateTabContentFactory(dock.TabIdPrefix, dock.ContentTemplate));
                    break;
            }
        }

        foreach (var item in Items)
        {
            switch (item)
            {
                case ToolWindowDefinition { IsOpen: true } window:
                    builder.Open(window.Id!); // a null id already failed in the first pass
                    break;
                case DocumentDefinition document:
                    builder.OpenDocument(RequireId(document.Id, nameof(DocumentDefinition)));
                    break;
                case PanelTabDefinition tab:
                    builder.OpenPanelTab(RequireId(tab.Id, nameof(PanelTabDefinition)));
                    break;
            }
        }

        return builder.Build();
    }

    private static ToolWindowDescriptor BuildDescriptor(ToolWindowDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Title))
        {
            throw new InvalidOperationException("A ToolWindowDefinition needs a non-empty Id and Title.");
        }

        if (definition.Content is not null && definition.ContentTemplate is not null)
        {
            throw new InvalidOperationException(
                $"Tool window '{definition.Id}' declares both Content and ContentTemplate; they are mutually exclusive.");
        }

        if (definition.Content is not null && definition.RetentionPolicy == ContentRetentionPolicy.DisposeOnClose)
        {
            // The markup singleton cannot be recreated after a release: the DisposeOnClose
            // contract demands fresh content per open cycle — a template is the
            // declarative factory that can deliver it.
            throw new InvalidOperationException(
                $"Tool window '{definition.Id}' combines direct Content with DisposeOnClose: a markup singleton cannot be recreated after a release — use ContentTemplate.");
        }

        if ((definition.TabIdPrefix is null) != (definition.TabTemplate is null))
        {
            throw new InvalidOperationException(
                $"Tool window '{definition.Id}' declares only one of TabIdPrefix and TabTemplate; the prefix is the ownership claim, the template creates the content.");
        }

        return new ToolWindowDescriptor(
            definition.Id, definition.Title, new ToolWindowSlot(definition.Side, definition.Group))
        {
            IconKey = definition.IconKey,
            DefaultOrder = definition.Order,
            DefaultMode = definition.Mode,
            DefaultPairRatio = definition.PairRatio,
            CreationPolicy = definition.CreationPolicy,
            RetentionPolicy = definition.RetentionPolicy,
            ContentFactory = definition.Content is { } content
                ? new SingletonToolWindowContentFactory(content)
                : definition.ContentTemplate is { } template
                    ? new TemplateToolWindowContentFactory(template)
                    : null,
            TabFactory = definition.TabIdPrefix is { } prefix
                ? new TemplateTabContentFactory(prefix, definition.TabTemplate!)
                : null,
        };
    }

    private static string RequireId(string? id, string itemKind) =>
        string.IsNullOrWhiteSpace(id)
            ? throw new InvalidOperationException($"A {itemKind} needs a non-empty Id.")
            : id;
}

/// <summary>Item of a <see cref="BerthLayoutDefinition"/> — a closed hierarchy of the markup vocabulary.</summary>
public abstract class LayoutDefinitionItem
{
    private protected LayoutDefinitionItem()
    {
    }
}

/// <summary>
/// One tool window registration in markup — the declarative view of
/// <see cref="ToolWindowDescriptor"/> plus the initial openness
/// (<see cref="IsOpen"/> — a build-time Open command, not a descriptor field).
/// Body content is declared one of two ways. <see cref="Content"/> — the markup child — is a
/// singleton created when the markup loads: honest for the default KeepWhileRegistered
/// retention, rejected under DisposeOnClose, which demands fresh content per open cycle. <see cref="ContentTemplate"/> is the declarative factory: built once per content
/// creation, so every lifecycle policy works; the template receives the tool window id as its
/// parameter. Tab claims are declared by <see cref="TabIdPrefix"/> — markup
/// cannot express arbitrary predicates — together with <see cref="TabTemplate"/>, which
/// receives the tab id and may decline a tab by building null.
/// </summary>
public sealed class ToolWindowDefinition : LayoutDefinitionItem
{
    /// <summary>Stable identifier; required.</summary>
    public string? Id { get; set; }

    /// <summary>Human-readable title; required.</summary>
    public string? Title { get; set; }

    /// <summary>Icon key resolved against application resources (ADR-0003).</summary>
    public string? IconKey { get; set; }

    /// <summary>Default side of the placement slot.</summary>
    public ToolWindowSide Side { get; set; }

    /// <summary>Default group of the placement slot.</summary>
    public ToolWindowGroup Group { get; set; }

    /// <summary>Default position within the slot; null — after the items declared earlier.</summary>
    public int? Order { get; set; }

    /// <summary>Default presentation mode.</summary>
    public ToolWindowMode Mode { get; set; }

    /// <summary>Default share preference within a side pair.</summary>
    public double PairRatio { get; set; } = LayoutDefaults.PairRatio;

    /// <summary>When the body content is created.</summary>
    public ContentCreationPolicy CreationPolicy { get; set; }

    /// <summary>How long the body content is retained. DisposeOnClose requires <see cref="ContentTemplate"/>.</summary>
    public ContentRetentionPolicy RetentionPolicy { get; set; }

    /// <summary>Whether the window is open in the initial state — a build-time Open command.</summary>
    public bool IsOpen { get; set; }

    /// <summary>
    /// Direct body content — the markup child: a control hosted as-is, or any object whose view
    /// the application's data templates build (the MVVM path). A markup singleton: legal under
    /// KeepWhileRegistered only. Mutually exclusive with <see cref="ContentTemplate"/>.
    /// </summary>
    [Content]
    public object? Content { get; set; }

    /// <summary>Declarative body factory: built once per content creation, the parameter is the window id.</summary>
    public IDataTemplate? ContentTemplate { get; set; }

    /// <summary>Ownership claim of this window's tabs by id prefix; comes together with <see cref="TabTemplate"/>.</summary>
    public string? TabIdPrefix { get; set; }

    /// <summary>Declarative tab factory: built per tab, the parameter is the tab id; building null declines the tab.</summary>
    public IDataTemplate? TabTemplate { get; set; }
}

/// <summary>
/// One dock content registration in markup: tabs whose id starts with
/// <see cref="TabIdPrefix"/> are documents, and <see cref="ContentTemplate"/> — the markup
/// child — creates their content, receiving the tab id as its parameter; building null
/// declines the tab. Both properties are required.
/// </summary>
public sealed class DockContentDefinition : LayoutDefinitionItem
{
    /// <summary>Ownership claim by id prefix; markup cannot express arbitrary predicates.</summary>
    public string? TabIdPrefix { get; set; }

    /// <summary>Declarative content factory of the claimed tabs.</summary>
    [Content]
    public IDataTemplate? ContentTemplate { get; set; }
}

/// <summary>A document open in the initial state — a build-time OpenDocument command.</summary>
public sealed class DocumentDefinition : LayoutDefinitionItem
{
    /// <summary>Id of the document tab; required.</summary>
    public string? Id { get; set; }
}

/// <summary>A panel tab open in the initial state — a build-time OpenPanelTab command.</summary>
public sealed class PanelTabDefinition : LayoutDefinitionItem
{
    /// <summary>Id of the panel tab; required. Its owner must be claimed by a tool window of the definition.</summary>
    public string? Id { get; set; }
}

/// <summary>
/// Body factory over a markup singleton: every creation returns the same instance — the
/// markup owns it — and release is a no-op. Valid under KeepWhileRegistered only; the
/// definition rejects the DisposeOnClose combination at build.
/// </summary>
internal sealed class SingletonToolWindowContentFactory(object content) : IToolWindowContentFactory
{
    public object CreateContent(string toolWindowId) => content;

    public void ReleaseContent(string toolWindowId, object content)
    {
    }
}

/// <summary>
/// Body factory over a data template — the declarative factory of markup: built once per
/// content creation, so DisposeOnClose recreation works; the body has no refusal
/// path, so a template building null is a configuration error.
/// </summary>
internal sealed class TemplateToolWindowContentFactory(IDataTemplate template) : IToolWindowContentFactory
{
    public object CreateContent(string toolWindowId) =>
        template.Build(toolWindowId)
        ?? throw new InvalidOperationException(
            $"The ContentTemplate of tool window '{toolWindowId}' built no control; the body has no refusal path.");

    public void ReleaseContent(string toolWindowId, object content)
    {
    }
}

/// <summary>
/// Tab factory over a prefix claim and a data template: the template is built
/// per tab with the tab id as its parameter; building null declines the tab, which is then
/// closed by a regular CloseTab.
/// </summary>
internal sealed class TemplateTabContentFactory(string prefix, IDataTemplate template) : ITabContentFactory
{
    public bool OwnsTab(string tabId) => tabId.StartsWith(prefix, StringComparison.Ordinal);

    public object? CreateContent(string tabId) => template.Build(tabId);

    public void ReleaseContent(string tabId, object content)
    {
    }
}
