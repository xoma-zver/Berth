<div align="center">

# Berth

**Docking library for [Avalonia](https://avaloniaui.net), built on the strict IntelliJ IDEA model.**

Six fixed tool-window slots, a document area shaped as a tree of splits with
tab groups, floating windows, and layout save/restore.
Deliberately **not** free-form docking: strict rules → an enumerable
state space → reliability.

[![NuGet](https://img.shields.io/nuget/v/Berth.Avalonia.svg?logo=nuget&label=Berth.Avalonia)](https://www.nuget.org/packages/Berth.Avalonia)
[![Downloads](https://img.shields.io/nuget/dt/Berth.Avalonia.svg)](https://www.nuget.org/packages/Berth.Avalonia)
[![CI](https://github.com/xoma-zver/Berth/actions/workflows/ci.yml/badge.svg)](https://github.com/xoma-zver/Berth/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.0-8b44ac.svg)](https://avaloniaui.net)
[![.NET](https://img.shields.io/badge/.NET-10.0-512bd4.svg)](https://dotnet.microsoft.com)

*Эта страница по-русски — [README.ru.md](README.ru.md).*

</div>

## The model at a glance

```text
┌──┬─────────────────────────────────┬──┐
│s │ ┌───────┬───────────┬─────────┐ │s │
│t │ │ left  │ dock area │  right  │ │t │
│r │ │ panel │(documents)│  panel  │ │r │
│i │ ├───────┴───────────┴─────────┤ │i │
│p │ │        bottom panel         │ │p │
│e │ └─────────────────────────────┘ │e │
└──┴─────────────────────────────────┴──┘
```

Three sides × two groups = **six slots** for tool windows; each window has **five display
modes**: dock pinned, dock unpinned, undock (an overlay taking no layout space), float
(a window above the main one) and window (an independent top-level). The document area is
an **n-ary tree of splits** with tab groups; panel tabs and documents share one tree model
and travel between hosts by the same commands.

## Why not free-form

Free-form docking allows arbitrary container nesting — and with it gets an unbounded state
space and uncountable edge cases: the chronic ailments of the genre. Berth takes the
opposite stance — the constrained IntelliJ IDEA model:

- **Enumerable by construction.** Tool windows exist only in six slots; arbitrary nesting
  is allowed only in the document area — and only as a tree normalized to canonical form.
  The invariants are pinned by property-based tests.
- **The spec is the truth.** Behaviour is defined by written specifications: every
  normative clause has a stable id (`TW-…`, `DA-…`, `INV-…`) and is covered by a test
  carrying the same id. Disputed points were verified line by line against the open
  intellij-community sources.
- **Commands, not gestures.** Every completed drag reduces to a core command that is also
  reachable from menus and code; until release a gesture is pure visualization, so
  cancellation costs nothing and leaves no trace in the state.

## Features

- **Tool windows** — six slots, five modes with per-layer eviction, focus-driven
  auto-hide, icon stripes with a quick-access “⋯” list, two-level menus, activation
  shortcuts; side and pair geometry in fractions — pixels never enter the core.
- **Document area** — an n-ary tree of splits with tab groups, normalized to canonical
  form; separate document windows; one tree model shared with tool-window content, so a
  tab travels between a panel and the document area by the same commands.
- **Floating layer** — real OS windows on the desktop, pseudo-windows over the workspace
  in the browser; the stored mode never degrades — only the effective presentation does.
- **Drag-and-drop** — dragging icons, headers and tabs within and across windows as a thin
  layer over core commands, with a rich visual language: content miniatures at the cursor,
  drop-zone previews, a live tab-strip reorder preview.
- **Persistence** — versioned JSON, `Apply` with a fix report, sleeping states for
  unregistered content, a layout reset that keeps open documents.
- **Composition** — a fluent builder in the core and declarative AXAML definitions
  (`BerthWorkspace.Definition`) — assembling a workspace without code.

## Quick start

```sh
dotnet add package Berth.Avalonia
```

Two packages: **Berth.Core** — the layout model and operations, free of UI-framework
dependencies (namespace `Berth`); **Berth.Avalonia** — the controls materializing the
model (namespace `Berth.Controls`). Installing the latter brings the core in as a
dependency.

A layout is composed with the builder and handed to `BerthWorkspace`:

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
    State = composition.State, // two-way: gestures assign command results back
};
```

Every user gesture is a core command; the result is assigned back to `State`, and the
application observes changes through a binding or
`GetObservable(BerthWorkspace.StateProperty)`.

<details>
<summary>The same workspace declaratively — in AXAML</summary>

```xml
<berth:BerthWorkspace xmlns:berth="using:Berth.Controls">
  <berth:BerthWorkspace.Definition>
    <berth:BerthLayoutDefinition>
      <berth:ToolWindowDefinition Id="project" Title="Project"
                                  Side="Left" Group="Primary" IsOpen="True">
        <views:ProjectView/>
      </berth:ToolWindowDefinition>
      <berth:DockContentDefinition TabIdPrefix="doc:">
        <berth:DockContentDefinition.ContentTemplate>
          <DataTemplate><views:DocumentView/></DataTemplate>
        </berth:DockContentDefinition.ContentTemplate>
      </berth:DockContentDefinition>
      <berth:DocumentDefinition Id="doc:README.md"/>
    </berth:BerthLayoutDefinition>
  </berth:BerthWorkspace.Definition>
</berth:BerthWorkspace>
```

</details>

Persistence is application policy over the core building blocks: `LayoutPersistence`
(JSON with a `SchemaVersion`), `Apply` with a fix report, `ResetToDefaults`, validation of
saved window bounds. The full guide: [docs/getting-started.md](docs/getting-started.md)
(in Russian).

## Platforms

| Platform | Support | Floating layer |
|---|---|---|
| Windows / macOS / Linux | full | real OS windows: float above the main one, independent window |
| Browser (WebAssembly) | full | pseudo-windows over the workspace |
| Android / iOS | best-effort builds | not materialized; touch behaviour is unspecified |

A window's stored mode never degrades: carried to a platform without the required
capabilities, only the effective presentation simplifies (`Window` → `Float` → `Undock`,
[ADR-0006](docs/adr/0006-platform-capabilities.md)) — back on a full platform the layout
behaves as before.

## Demo

`samples/Berth.Demo` is a mini-IDE: panels exercising both content paths (MVVM and control
factories), documents, terminal tabs, shortcuts, layout reset and debounced autosave.

```sh
dotnet run --project samples/Berth.Demo.Desktop   # desktop: file persistence, real windows

dotnet workload install wasm-tools                # once, for the browser host
dotnet run --project samples/Berth.Demo.Browser   # browser: http://localhost:5235, localStorage
```

## Architecture and method

- **A core without UI.** `Berth.Core` does not reference Avalonia — the boundary is pinned
  by an architecture test. All layout logic is pure transitions of immutable state; the UI
  layer only materializes the state and turns input into commands
  ([ADR-0002](docs/adr/0002-core-avalonia-layering.md)).
- **The layout stores identifiers only.** Content is created by application factories —
  lazy creation, sleeping states, resilient restore
  ([ADR-0003](docs/adr/0003-layout-content-separation.md)).
- **Drag-and-drop over commands.** The library is functionally complete without it:
  everything is reachable from menus and code
  ([ADR-0004](docs/adr/0004-dnd-over-commands.md)).
- **Pixels stay at the UI boundary.** The core deals in weights and fractions; screen
  coordinates are validated on restore.

Architectural decisions are recorded in [docs/adr/](docs/adr/).

## Status

**0.1.x (pre-1.0): v1 functionality is complete.** The public API and the persistence
schema may still move; API changes go through an explicit `PublicAPI.Unshipped.txt` diff
as part of review.

## Documentation

Project documentation is currently written in Russian.

- [docs/](docs/README.md) — the documentation map
- [getting-started.md](docs/getting-started.md) — the applied guide: installation, composition, wiring, persistence
- Behavioural specifications — the source of truth for what must happen:
  [tool-windows](docs/spec/tool-windows.md), [document-area](docs/spec/document-area.md)
- [docs/adr/](docs/adr/) — architecture decision records
- [docs/reference/](docs/reference/) — how the reference (IntelliJ IDEA) works and where Berth deliberately diverges
- [docs/BACKLOG.md](docs/BACKLOG.md) — the work plan

## Repository

The solution is `Berth.slnx`: the library in `src/`, tests — core unit and property
tests, headless UI tests, demo integration tests — in `tests/`, demo hosts in `samples/`.
CI checks formatting, runs all three test suites, builds the desktop demo and packs the
NuGet packages; the build treats warnings as errors. The repository folder is still
historically named `IdeaDocking`.

## License

[MIT](LICENSE) © 2026 Sergey Zhdanov
