using System.Globalization;
using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Persistence format (spec TW-10.1, TW-10.2, TW-10.5, DA-9.5): golden files pin the exact
/// document byte-for-byte, the domain boundary separates loadable value defects from format
/// errors, unknown fields are ignored per the schema evolution rule.
/// </summary>
public class PersistenceTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);
    private static readonly ToolWindowSlot RightPrimary = new(ToolWindowSide.Right, ToolWindowGroup.Primary);
    private static readonly ToolWindowSlot BottomSecondary = new(ToolWindowSide.Bottom, ToolWindowGroup.Secondary);

    private static string ReadGolden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name)).TrimEnd('\n');

    private static TabGroupNode Group(string active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    /// <summary>
    /// A deterministic, valid state with every serialized field at a non-default value — the
    /// source of the rich golden file and of the witness round-trip (ApplyPropertyTests).
    /// </summary>
    internal static LayoutState RichState() => LayoutState.Empty with
    {
        ToolWindows =
        [
            new ToolWindowState("project", LeftPrimary, 0) with { IsOpen = true, PairRatio = 0.75 },
            new ToolWindowState("structure", LeftPrimary, 1) with
            {
                Mode = ToolWindowMode.Undock,
                LastInternalMode = ToolWindowMode.Undock,
                UndockWeight = 0.25,
            },
            new ToolWindowState("terminal", BottomSecondary, 0) with
            {
                Mode = ToolWindowMode.Window,
                FloatingBounds = new FloatingBounds(10, 20, 640, 480),
            },
            new ToolWindowState("sleeper", RightPrimary, 0) with { IsIconVisible = false },
        ],
        Left = new SideState(0.25, 0.75),
        Right = new SideState(0.3, 0.6),
        Bottom = new SideState(0.45, 0.55),
        QuickAccessSide = QuickAccessSide.Right,
        ActiveToolWindowId = "project",
        DockArea = new DockAreaState
        {
            Root = new SplitNode
            {
                Orientation = SplitOrientation.Row,
                Children =
                [
                    new SplitChild(Group("doc1", "doc1", "doc2"), 0.25),
                    new SplitChild(
                        new SplitNode
                        {
                            Orientation = SplitOrientation.Column,
                            Children =
                            [
                                new SplitChild(Group("doc3", "doc3"), 0.5),
                                new SplitChild(Group("doc4", "doc4"), 0.5),
                            ],
                        },
                        0.75),
                ],
            },
            CurrentTabId = "doc1",
            Windows =
            [
                new DocumentWindowState(new FloatingBounds(100, 100, 800, 600), Group("doc5", "doc5"), "doc5"),
            ],
            ActiveDockHost = DockHost.DocumentWindow(0),
        },
    };

    // ---- TW-10.1 golden files ----

    [Fact]
    public void TW_10_1_golden_empty_layout_matches_and_round_trips()
    {
        var golden = ReadGolden("empty-layout.json");

        Assert.Equal(golden, LayoutPersistence.Serialize(LayoutState.Empty));

        var result = LayoutState.Empty.Apply(
            LayoutPersistence.Deserialize(golden), ApplyScope.Full, new ToolWindowRegistry());
        Assert.Empty(result.Fixes);
        Assert.Equal(golden, LayoutPersistence.Serialize(result.State));
    }

    [Fact]
    public void TW_10_1_golden_rich_layout_matches_and_round_trips()
    {
        var golden = ReadGolden("rich-layout.json");

        Assert.Equal(golden, LayoutPersistence.Serialize(RichState()));

        var result = LayoutState.Empty.Apply(
            LayoutPersistence.Deserialize(golden), ApplyScope.Full, new ToolWindowRegistry());
        Assert.Empty(result.Fixes);
        Assert.Equal(golden, LayoutPersistence.Serialize(result.State));
    }

    [Fact]
    public void TW_10_1_serialization_does_not_depend_on_the_thread_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // ru-RU uses a decimal comma; the format must stay culture-invariant.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");

            var golden = ReadGolden("rich-layout.json");
            Assert.Equal(golden, LayoutPersistence.Serialize(RichState()));
            Assert.Equal(golden, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(golden)));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // ---- TW-10.5 load errors: format and version ----

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{ "schemaVersion": 2 }""")]
    [InlineData("""{ "schemaVersion": "abc" }""")]
    [InlineData("""{ "schemaVersion": null }""")]
    public void TW_10_5_unparseable_document_or_unsupported_version_is_a_load_error(string json)
    {
        Assert.Throws<LayoutFormatException>(() => LayoutPersistence.Deserialize(json));
    }

    // ---- TW-10.5 load errors: values outside the model domain ----

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "toolWindows": [{ "id": "a", "mode": "banana" }] }""")]
    [InlineData("""{ "schemaVersion": 1, "toolWindows": [{ "id": "a", "side": "top" }] }""")]
    [InlineData("""{ "schemaVersion": 1, "toolWindows": [{ "id": "" }] }""")]
    [InlineData("""{ "schemaVersion": 1, "toolWindows": [{ "order": 0 }] }""")]
    [InlineData("""{ "schemaVersion": 1, "toolWindows": [{ "id": "a", "isOpen": "yes" }] }""")]
    [InlineData("""{ "schemaVersion": 1, "toolWindows": ["a"] }""")]
    [InlineData("""{ "schemaVersion": 1, "quickAccessSide": "bottom" }""")]
    [InlineData("""{ "schemaVersion": 1, "activeToolWindowId": 5 }""")]
    [InlineData("""{ "schemaVersion": 1, "dockArea": { "root": { "type": "group", "tabs": [1] } } }""")]
    [InlineData("""{ "schemaVersion": 1, "dockArea": { "root": { "type": "group", "tabs": [""] } } }""")]
    [InlineData("""{ "schemaVersion": 1, "dockArea": { "root": { "type": "grid" } } }""")]
    [InlineData("""{ "schemaVersion": 1, "dockArea": { "root": { "tabs": [] } } }""")]
    [InlineData("""{ "schemaVersion": 1, "dockArea": { "root": { "type": "split", "children": [{ "share": 0.5 }] } } }""")]
    [InlineData("""{ "schemaVersion": 1, "dockArea": { "activeDockHost": -1 } }""")]
    [InlineData("""{ "schemaVersion": 1, "dockArea": { "activeDockHost": "main" } }""")]
    public void TW_10_5_value_outside_the_model_domain_is_a_load_error(string json)
    {
        Assert.Throws<LayoutFormatException>(() => LayoutPersistence.Deserialize(json));
    }

    [Theory]
    [InlineData("""{ "bounds": { "x": 0, "y": 0, "width": 100, "height": 100 }, "root": { "type": "group", "tabs": ["w"], "activeTabId": "w" } }""")]
    [InlineData("""{ "bounds": { "x": 0, "y": 0, "width": 100, "height": 100 }, "root": { "type": "group", "tabs": ["w"], "activeTabId": "w" }, "currentTabId": null }""")]
    [InlineData("""{ "bounds": { "x": 0, "y": 0, "width": 100, "height": 100 }, "root": { "type": "group", "tabs": ["w"], "activeTabId": "w" }, "currentTabId": "" }""")]
    [InlineData("""{ "root": { "type": "group", "tabs": ["w"], "activeTabId": "w" }, "currentTabId": "w" }""")]
    [InlineData("""{ "bounds": { "x": "abc", "y": 0, "width": 100, "height": 100 }, "root": { "type": "group", "tabs": ["w"], "activeTabId": "w" }, "currentTabId": "w" }""")]
    [InlineData("""{ "bounds": { "y": 0, "width": 100, "height": 100 }, "root": { "type": "group", "tabs": ["w"], "activeTabId": "w" }, "currentTabId": "w" }""")]
    public void DA_9_5_document_window_without_mandatory_parts_is_a_load_error(string window)
    {
        var json = $$"""{ "schemaVersion": 1, "dockArea": { "windows": [{{window}}] } }""";

        Assert.Throws<LayoutFormatException>(() => LayoutPersistence.Deserialize(json));
    }

    // ---- TW-10.5 lenient value defects: loadable, repaired by Apply with a report ----

    [Fact]
    public void TW_10_5_non_numeric_fraction_loads_as_nan_and_is_repaired()
    {
        var json = """{ "schemaVersion": 1, "toolWindows": [{ "id": "a", "pairRatio": "abc" }] }""";

        var raw = LayoutPersistence.Deserialize(json);
        Assert.True(double.IsNaN(raw.ToolWindows[0].PairRatio));

        var result = LayoutState.Empty.Apply(raw, ApplyScope.Full, new ToolWindowRegistry());
        Assert.Equal(LayoutDefaults.PairRatio, result.State.ToolWindows[0].PairRatio);
        Assert.Equal(["INV-4"], result.Fixes.Select(f => f.Rule).ToArray());
    }

    [Fact]
    public void TW_10_5_non_integer_order_loads_as_invalid_and_is_compacted()
    {
        var json = """{ "schemaVersion": 1, "toolWindows": [{ "id": "a", "order": "abc" }] }""";

        var raw = LayoutPersistence.Deserialize(json);
        Assert.Equal(-1, raw.ToolWindows[0].Order);

        var result = LayoutState.Empty.Apply(raw, ApplyScope.Full, new ToolWindowRegistry());
        Assert.Equal(0, result.State.ToolWindows[0].Order);
        Assert.Equal(["INV-3"], result.Fixes.Select(f => f.Rule).ToArray());
    }

    [Fact]
    public void TW_10_5_non_numeric_floating_bounds_load_as_nan_and_are_reset()
    {
        var json = """{ "schemaVersion": 1, "toolWindows": [{ "id": "a", "floatingBounds": "abc" }] }""";

        var raw = LayoutPersistence.Deserialize(json);
        Assert.True(double.IsNaN(raw.ToolWindows[0].FloatingBounds!.Value.X));

        var result = LayoutState.Empty.Apply(raw, ApplyScope.Full, new ToolWindowRegistry());
        Assert.Null(result.State.ToolWindows[0].FloatingBounds);
        Assert.Equal(["TW-10.4"], result.Fixes.Select(f => f.Rule).ToArray());
    }

    [Fact]
    public void TW_10_5_dangling_references_load_and_are_repaired()
    {
        var json = """
            {
              "schemaVersion": 1,
              "activeToolWindowId": "ghost",
              "dockArea": { "root": { "type": "group", "tabs": ["a"], "activeTabId": "a" }, "currentTabId": "ghost" }
            }
            """;

        var result = LayoutState.Empty.Apply(
            LayoutPersistence.Deserialize(json), ApplyScope.Full, new ToolWindowRegistry());

        Assert.Null(result.State.ActiveToolWindowId);
        Assert.Equal("a", result.State.DockArea.CurrentTabId);
        Assert.Equal(["INV-5", "INV-D4"], result.Fixes.Select(f => f.Rule).Order(StringComparer.Ordinal).ToArray());
    }

    // ---- TW-10.5 schema evolution: unknown fields are ignored ----

    [Fact]
    public void TW_10_5_unknown_fields_are_ignored_on_read()
    {
        var json = """
            {
              "schemaVersion": 1,
              "futureTopLevel": { "anything": [1, 2, 3] },
              "toolWindows": [{ "id": "a", "futureField": true, "contentTree": { "type": "group" } }],
              "dockArea": {
                "root": { "type": "group", "tabs": ["d"], "activeTabId": "d", "futureNodeField": 7 },
                "currentTabId": "d"
              }
            }
            """;
        var plain = """
            {
              "schemaVersion": 1,
              "toolWindows": [{ "id": "a" }],
              "dockArea": { "root": { "type": "group", "tabs": ["d"], "activeTabId": "d" }, "currentTabId": "d" }
            }
            """;

        Assert.Equal(
            LayoutPersistence.Serialize(LayoutPersistence.Deserialize(plain)),
            LayoutPersistence.Serialize(LayoutPersistence.Deserialize(json)));
    }

    // ---- TW-10.2 sleeping entities round-trip ----

    [Fact]
    public void TW_10_2_E12_unknown_id_sleeps_through_apply_and_serialization()
    {
        var snapshot = LayoutState.Empty with
        {
            ToolWindows = [new ToolWindowState("x", LeftPrimary, 0) with { PairRatio = 0.7 }],
        };

        // The registry knows nothing about "x": the state sleeps, is not repaired, and is written back.
        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, new ToolWindowRegistry());

        Assert.Empty(result.Fixes);
        Assert.Equal(0.7, result.State.ToolWindows[0].PairRatio);
        Assert.Contains("\"x\"", LayoutPersistence.Serialize(result.State), StringComparison.Ordinal);
    }
}
