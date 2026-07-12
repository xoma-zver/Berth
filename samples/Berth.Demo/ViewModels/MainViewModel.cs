using System;
using System.Collections.Immutable;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Berth.Demo.ViewModels;

/// <summary>
/// Composition root of the mini-IDE demo: registers demo tool windows exercising both content
/// paths (a view model resolved by the ViewLocator, and factory-built controls) and both
/// lifecycle axes (Eager/OnFirstOpen creation, KeepWhileRegistered/DisposeOnClose retention),
/// registers dock-area content claimed by the "doc:" prefix (spec TW-9.11), then builds the
/// initial layout with two open documents. The workspace binds State two-way, so user gestures
/// flow back into this property.
///
/// The view model is also the reference sample of application-side persistence (task 7.0): the
/// core supplies the bricks — <see cref="LayoutPersistence"/> for the format,
/// <see cref="LayoutApply.Apply"/> for normalization with a report, validators for saved
/// bounds — and the application adds only policy: where the document lives
/// (<see cref="ILayoutStore"/>, injected by the host), when to write it (a debounced autosave
/// plus an explicit save on window closing, TW-7.5), and how to react to a bad document
/// (<see cref="LayoutFormatException"/> → stay on the default composition; the migration chain
/// arrives with the first real SchemaVersion bump — spec TW-10.5, section 12). Gestures are
/// pure visualization until their commit (ADR-0004), so the debounced autosave can never
/// observe a half-dragged state.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private LayoutState? _state;

    private ILayoutStore? _store;
    private DispatcherTimer? _autosave;

    public MainViewModel()
    {
        Registry = new ToolWindowRegistry();
        Lifecycle = new ContentLifecycle(Registry);

        var state = Lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "project", "Project", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            IconKey = "ProjectIcon",
            CreationPolicy = ContentCreationPolicy.Eager,
            // The MVVM path: the factory returns a view model, the application's ViewLocator
            // (App.axaml data templates) builds the view.
            ContentFactory = new DelegateContentFactory(_ => new ProjectViewModel
            {
                OpenFile = OpenFileDocument,
            }),
        });
        state = Lifecycle.Register(state, new ToolWindowDescriptor(
            "structure", "Structure", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Secondary))
        {
            ContentFactory = new DelegateContentFactory(_ => new TextBlock
            {
                Text = "Structure of the selected file would appear here.",
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap,
            }),
        });
        state = Lifecycle.Register(state, new ToolWindowDescriptor(
            "terminal", "Terminal", new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Primary))
        {
            RetentionPolicy = ContentRetentionPolicy.DisposeOnClose,
            ContentFactory = new DelegateContentFactory(_ => new TextBox
            {
                Text = "$ echo DisposeOnClose — closing the panel forgets this text\n",
                AcceptsReturn = true,
                FontFamily = new FontFamily("monospace"),
            }),
            // Panel tabs claimed by prefix (TW-9.11): the terminal's own sessions living in
            // its tree next to the body — the strip, splits and «Move to Document Area» demo.
            TabFactory = new DelegateTabFactory(
                id => id.StartsWith("term:", StringComparison.Ordinal),
                id => new TextBox
                {
                    Text = $"$ {id[5..]} — a terminal session tab\n",
                    AcceptsReturn = true,
                    FontFamily = new FontFamily("monospace"),
                }),
        });
        state = Lifecycle.Register(state, new ToolWindowDescriptor(
            "problems", "Problems", new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Secondary))
        {
            // A diagnostics list driving the command channel from application code:
            // double-clicking an entry opens (or activates) the offending document.
            ContentFactory = new DelegateContentFactory(_ => BuildProblemsView()),
        });
        state = Lifecycle.Register(state, new ToolWindowDescriptor(
            "properties", "Properties", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary))
        {
            ContentFactory = new DelegateContentFactory(_ => new TextBox
            {
                Text = "KeepWhileRegistered — this text survives close and reopen\n",
                AcceptsReturn = true,
            }),
        });

        // Dock-area documents: claimed by prefix (TW-9.11), materialized lazily by the
        // workspace (DA-9.3); titles come from the TabTitleProvider wired in MainView.
        state = Lifecycle.RegisterDockContent(state, new DelegateTabFactory(
            id => id.StartsWith("doc:", StringComparison.Ordinal),
            id => new TextBox
            {
                Text = $"// {id[4..]}\nOpen a second document, split and rotate via the tab menu.\n",
                AcceptsReturn = true,
                FontFamily = new FontFamily("monospace"),
            }));

        // Application-performed transitions are the application's to report, one call per
        // command (the NotifyTransition contract).
        var opened = state.Open("project");
        Lifecycle.NotifyTransition(state, opened);
        var withTerminal = opened.Open("terminal");
        Lifecycle.NotifyTransition(opened, withTerminal);
        var withReadme = withTerminal.OpenDocument("doc:README.md", Registry);
        Lifecycle.NotifyTransition(withTerminal, withReadme);
        var withSpec = withReadme.OpenDocument("doc:tool-windows.md", Registry);
        Lifecycle.NotifyTransition(withReadme, withSpec);
        var withLocal = withSpec.OpenPanelTab("term:Local", Registry);
        Lifecycle.NotifyTransition(withSpec, withLocal);
        var withSecond = withLocal.OpenPanelTab("term:Local (2)", Registry);
        Lifecycle.NotifyTransition(withLocal, withSecond);
        State = withSecond;
    }

    public ToolWindowRegistry Registry { get; }

    public ContentLifecycle Lifecycle { get; }

    /// <summary>Report of the last restore (spec TW-10.4): the fixes Apply performed on the stored document.</summary>
    public ImmutableArray<AppliedFix> LastRestoreFixes { get; private set; } = [];

    /// <summary>Why the last restore fell back to the default composition, or null (spec TW-10.5).</summary>
    public string? LastRestoreError { get; private set; }

    /// <summary>
    /// Attaches the host-supplied store: restores the stored layout — an unreadable or
    /// unsupported document keeps the default composition (TW-10.5; the strict reaction the
    /// spec suggests, ResetToDefaults, is what the constructor already built) — and enables
    /// the debounced autosave. The optional validator heals saved screen bounds (TW-7.4,
    /// DA-7.4): the desktop host passes <see cref="FloatingBoundsValidation.CreateValidator"/>,
    /// the browser one — <see cref="FloatingBoundsValidation.CreateOverlayValidator"/>.
    /// </summary>
    public void AttachPersistence(ILayoutStore store, BoundsValidator? validateBounds)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        RestoreLayout(validateBounds);
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autosave.Tick += (_, _) =>
        {
            _autosave.Stop();
            SaveLayout();
        };
    }

    /// <summary>
    /// Writes the current layout to the store — the immutable state is its own snapshot
    /// (TW-5.14). Called by the debounced autosave and by the host on window closing
    /// (TW-7.5: the guaranteed write before teardown). A no-op without a store or a state.
    /// </summary>
    public void SaveLayout()
    {
        if (_store is { } store && State is { } state)
        {
            store.Save(LayoutPersistence.Serialize(state));
        }
    }

    /// <summary>
    /// The «Reset Layout» menu command: re-applies the descriptor defaults as an Arrangement —
    /// panels return to their default slots and close, while open documents, tabs and content
    /// trees stay untouched (TW-10.6, E20).
    /// </summary>
    public void ResetLayout()
    {
        if (State is not { } state)
        {
            return;
        }

        var result = state.Apply(LayoutApply.ResetToDefaults(Registry), ApplyScope.Arrangement, Registry);
        Lifecycle.NotifyTransition(state, result.State);
        State = result.State;
    }

    /// <summary>
    /// Opens a project file as a document through the command channel (DA-5.1): an already
    /// open document is only activated. One command, one lifecycle report (ADR-0004).
    /// </summary>
    public void OpenFileDocument(string file)
    {
        if (State is not { } state || string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        var result = state.OpenDocument($"doc:{file}", Registry);
        Lifecycle.NotifyTransition(state, result);
        State = result;
    }

    private void RestoreLayout(BoundsValidator? validateBounds)
    {
        if (_store!.Load() is not { } json || State is not { } current)
        {
            return;
        }

        LayoutState snapshot;
        try
        {
            snapshot = LayoutPersistence.Deserialize(json);
        }
        catch (LayoutFormatException exception)
        {
            // The explicit load error of TW-10.5: an unparseable document or an unsupported
            // SchemaVersion (the migration chain arrives with SchemaVersion 2 — spec section
            // 12). The demo stays on the default composition the constructor built.
            LastRestoreError = exception.Message;
            Trace.TraceWarning("Berth.Demo: stored layout rejected, starting from defaults: {0}", exception.Message);
            return;
        }

        var result = current.Apply(snapshot, ApplyScope.Full, Registry, validateBounds);
        LastRestoreFixes = result.Fixes;
        foreach (var fix in result.Fixes)
        {
            Trace.TraceInformation("Berth.Demo: layout fix [{0}] {1}", fix.Rule, fix.Message);
        }

        // An Apply is one application-performed transition (the NotifyTransition contract).
        Lifecycle.NotifyTransition(current, result.State);
        State = result.State;
    }

    /// <summary>
    /// Debounced autosave: every committed state change re-arms the timer, so bursts collapse
    /// into one write. Gestures commit atomically (ADR-0004), so whatever the timer observes
    /// is a complete, serializable state. Inert until <see cref="AttachPersistence"/>.
    /// </summary>
    partial void OnStateChanged(LayoutState? value)
    {
        if (_autosave is { } timer && value is not null)
        {
            timer.Stop();
            timer.Start();
        }
    }

    private sealed record ProblemItem(string File, string Message)
    {
        public override string ToString() => $"{File}: {Message}";
    }

    private Control BuildProblemsView()
    {
        var list = new ListBox
        {
            ItemsSource = new[]
            {
                new ProblemItem("README.md", "Warning: the status paragraph is one sentence long"),
                new ProblemItem("tool-windows.md", "Typo in the terminology section"),
                new ProblemItem("README.md", "Info: consider a table of contents"),
            },
        };
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is ProblemItem problem)
            {
                OpenFileDocument(problem.File);
            }
        };
        return list;
    }
}
