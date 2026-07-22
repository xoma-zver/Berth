# Berth

Docking library for [Avalonia](https://avaloniaui.net) modelled on IntelliJ IDEA: six fixed
tool-window slots (3 sides × 2 groups), five display modes (dock pinned / dock unpinned /
undock / float / window), a document area as a tree of splits with tab groups, floating
windows and layout save/restore. Deliberately **not** free-form docking — strict rules give
an enumerable state space and, with it, reliability: behaviour is defined by written specs,
and every normative clause is covered by a test carrying the same id.

<img src="https://raw.githubusercontent.com/xoma-zver/Berth/master/docs/assets/berth-hero-light.png" width="720" alt="Berth layout model: icon stripes, docked tool windows, panel tabs, the document area with a split, and a floating overlay above the layout">

## Packages

- **Berth.Core** — the layout model and operations: pure, serializable state and pure
  transitions. No UI-framework dependency; namespace `Berth`.
- **Berth.Avalonia** — the Avalonia controls that materialize the model. Depends on
  Berth.Core; namespace `Berth.Controls`.
- **Berth.Theme.NewUi** — an optional ready-made appearance styled after the IntelliJ
  IDEA New UI look, light and dark. Depends on Berth.Avalonia; namespace
  `Berth.Themes.NewUi`; one line to enable: `<newui:BerthNewUiTheme/>` in application
  styles.

Install the controls (Berth.Core comes transitively):

```sh
dotnet add package Berth.Avalonia
```

## Highlights

- **Tool windows** — six slots, five modes with per-layer eviction, focus-driven auto-hide,
  stripes with a quick-access “⋯” list, two-level menus, application-bound activation
  shortcuts; side and pair geometry in fractions — pixels never enter the core.
- **Document area** — an n-ary tree of splits with tab groups, normalized to canonical
  form; document windows; one tree model shared with tool-window content, so tabs travel
  between a panel and the dock area by the same commands.
- **Floating layer** — real OS windows on the desktop, pseudo-windows in the browser
  overlay; the stored mode never degrades, only the effective presentation
  (`CanFloat`/`CanUseWindowed`).
- **Drag-and-drop** — a thin layer over core commands (every gesture is also reachable from
  menus and code), with ghost miniatures, drop-zone previews and a live tab-strip reorder
  preview.
- **Persistence** — versioned JSON, `Apply` with a fix report, sleeping states for
  unregistered content, layout reset that keeps open documents.
- **Composition** — a fluent builder in the core and declarative AXAML definitions
  (`BerthWorkspace.Definition`).

## Minimal use

Compose a layout with the fluent builder and hand its parts to `BerthWorkspace`:

```csharp
var composition = new LayoutCompositionBuilder()
    .AddToolWindow("project", "Project", w => w
        .Slot(ToolWindowSide.Left, ToolWindowGroup.Primary)
        .Content(_ => new TextBlock { Text = "Project tree" }))
    .AddDockContent(
        id => id.StartsWith("doc:", StringComparison.Ordinal),
        id => new TextBox { Text = $"// {id[4..]}", AcceptsReturn = true })
    .Open("project")
    .OpenDocument("doc:README.md")
    .Build();

var workspace = new BerthWorkspace
{
    Registry = composition.Registry,
    Lifecycle = composition.Lifecycle,
    State = composition.State, // bindable two-way; gestures assign the command result back
};
```

Or declare the default layout in AXAML via `BerthWorkspace.Definition`
(`BerthLayoutDefinition`). Persistence is application policy over the core bricks
(`LayoutPersistence`, `Apply` with a fix report, `ResetToDefaults`,
`FloatingBoundsValidation`).

## Platforms

Desktop (Windows, macOS, Linux) and browser (WebAssembly; floating panels and document
windows become overlay pseudo-windows). Mobile targets build best-effort; touch docking
behaviour is unspecified.

## Status

Pre-1.0 (0.1.x): v1 functionality is complete; the public API and the persistence schema
may still move.

## Links

- Repository, docs and specs: https://github.com/xoma-zver/Berth
- Getting started, behavioural specs (`docs/spec/`) and ADRs (`docs/adr/`) live in the repo.

Licensed under the MIT License.
