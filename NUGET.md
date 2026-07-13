# Berth

Docking library for [Avalonia](https://avaloniaui.net) modelled on IntelliJ IDEA: six fixed
tool-window slots (3 sides × 2 groups), five display modes (dock pinned / dock unpinned /
undock / float / window), a document area as a tree of splits with tab groups, and layout
save/restore. Deliberately **not** free-form docking — strict rules give an enumerable state
space and, with it, reliability.

## Packages

- **Berth.Core** — the layout model and operations. No UI-framework dependency; pure,
  serializable state and pure transitions.
- **Berth.Avalonia** — the Avalonia controls that materialize the model. Depends on
  Berth.Core; namespace `Berth.Controls`.

Install the controls (Berth.Core comes with them):

```sh
dotnet add package Berth.Avalonia
```

## Minimal use

Compose a layout with the fluent builder and hand its parts to `BerthWorkspace`:

```csharp
var composition = new LayoutCompositionBuilder()
    .AddToolWindow("project", "Project", w => w
        .Slot(ToolWindowSide.Left, ToolWindowGroup.Primary)
        .Content(_ => new ProjectView()))
    .AddDockContent(
        id => id.StartsWith("doc:", StringComparison.Ordinal),
        id => new TextBox { Text = id[4..] })
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

## Status

Pre-1.0 (0.1.x): the public API and the persistence schema may still move.

## Links

- Repository, docs and specs: https://github.com/xoma-zver/Berth
- Getting started, behavioural specs (`docs/spec/`) and ADRs (`docs/adr/`) live in the repo.

Licensed under the MIT License.
