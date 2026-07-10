using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Berth;

/// <summary>
/// JSON serialization of <see cref="LayoutState"/> (spec TW-10.1, TW-10.5, DA-9.5). The wire
/// format is hand-mapped over the System.Text.Json reader and writer — no binding layer — so
/// it is pinned here explicitly, never follows C# refactorings, and is guarded by golden tests.
/// <see cref="Deserialize"/> returns the raw state without repairs: value-level defects the
/// model can represent — a non-numeric number (→ NaN), a dangling reference, a negative
/// order — load as-is and are repaired by <see cref="LayoutApply.Apply"/> with a report, while
/// values outside the domain of the typed model raise <see cref="LayoutFormatException"/>
/// (TW-10.5). Unknown fields are ignored on read (the schema evolution rule of TW-10.5);
/// unknown entities round-trip as sleeping states and sleeping tabs (TW-10.2, DA-9.4).
/// </summary>
public static class LayoutPersistence
{
    /// <summary>Version of the document schema written by <see cref="Serialize"/> and required by <see cref="Deserialize"/> (spec TW-10.1, TW-10.5).</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n", // golden files are byte-stable across operating systems
    };

    private static readonly Dictionary<string, ToolWindowSide> SideValues = new(StringComparer.Ordinal)
    {
        ["left"] = ToolWindowSide.Left,
        ["right"] = ToolWindowSide.Right,
        ["bottom"] = ToolWindowSide.Bottom,
    };

    private static readonly Dictionary<string, ToolWindowGroup> GroupValues = new(StringComparer.Ordinal)
    {
        ["primary"] = ToolWindowGroup.Primary,
        ["secondary"] = ToolWindowGroup.Secondary,
    };

    private static readonly Dictionary<string, ToolWindowMode> ModeValues = new(StringComparer.Ordinal)
    {
        ["dockPinned"] = ToolWindowMode.DockPinned,
        ["dockUnpinned"] = ToolWindowMode.DockUnpinned,
        ["undock"] = ToolWindowMode.Undock,
        ["float"] = ToolWindowMode.Float,
        ["window"] = ToolWindowMode.Window,
    };

    private static readonly Dictionary<string, QuickAccessSide> QuickAccessValues = new(StringComparer.Ordinal)
    {
        ["left"] = QuickAccessSide.Left,
        ["right"] = QuickAccessSide.Right,
    };

    private static readonly Dictionary<string, SplitOrientation> OrientationValues = new(StringComparer.Ordinal)
    {
        ["row"] = SplitOrientation.Row,
        ["column"] = SplitOrientation.Column,
    };

    /// <summary>Serializes a layout to the versioned JSON document (spec TW-10.1). The output is deterministic: fixed property order, two-space indentation, LF line endings, culture-invariant numbers (golden tests).</summary>
    /// <param name="state">The layout to serialize; core-produced states are always serializable, non-finite numbers in a hand-constructed state are a caller error.</param>
    public static string Serialize(LayoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteLayout(writer, state);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Parses the versioned JSON document into a raw <see cref="LayoutState"/> (spec TW-10.5,
    /// DA-9.5). The result is not repaired — pass it through <see cref="LayoutApply.Apply"/>,
    /// the single normalization gate. Fields absent from the document take their defaults;
    /// unknown fields are ignored.
    /// </summary>
    /// <param name="json">The document text.</param>
    /// <exception cref="LayoutFormatException">The document is not parseable JSON, its root is not an object, the schema version is missing or unsupported, or a value lies outside the domain of the typed model.</exception>
    public static LayoutState Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new LayoutFormatException("The layout document is not parseable JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new LayoutFormatException("The layout document root must be a JSON object.");
            }

            var version = ReadSchemaVersion(root);
            if (version != SchemaVersion)
            {
                throw new LayoutFormatException(
                    $"Schema version {version} is not supported; this build reads version {SchemaVersion}.");
            }

            return ReadLayout(root);
        }
    }

    // ---- writing ----

    private static void WriteLayout(Utf8JsonWriter writer, LayoutState state)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);

        writer.WriteStartArray("toolWindows");
        foreach (var window in state.ToolWindows)
        {
            WriteToolWindow(writer, window);
        }

        writer.WriteEndArray();

        writer.WriteStartObject("sides");
        WriteSide(writer, "left", state.Left);
        WriteSide(writer, "right", state.Right);
        WriteSide(writer, "bottom", state.Bottom);
        writer.WriteEndObject();

        writer.WriteString("quickAccessSide", state.QuickAccessSide == QuickAccessSide.Left ? "left" : "right");
        WriteNullableString(writer, "activeToolWindowId", state.ActiveToolWindowId);

        writer.WriteStartObject("dockArea");
        writer.WritePropertyName("root");
        WriteNode(writer, state.DockArea.Root);
        WriteNullableString(writer, "currentTabId", state.DockArea.CurrentTabId);
        writer.WriteStartArray("windows");
        foreach (var window in state.DockArea.Windows)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("bounds");
            WriteBounds(writer, window.Bounds);
            writer.WritePropertyName("root");
            WriteNode(writer, window.Root);
            writer.WriteString("currentTabId", window.CurrentTabId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (state.DockArea.ActiveDockHost.DocumentWindowIndex is { } index)
        {
            writer.WriteNumber("activeDockHost", index);
        }
        else
        {
            writer.WriteNull("activeDockHost");
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteToolWindow(Utf8JsonWriter writer, ToolWindowState window)
    {
        writer.WriteStartObject();
        writer.WriteString("id", window.Id);
        writer.WriteString("side", SideName(window.Slot.Side));
        writer.WriteString("group", window.Slot.Group == ToolWindowGroup.Primary ? "primary" : "secondary");
        writer.WriteNumber("order", window.Order);
        writer.WriteString("mode", ModeName(window.Mode));
        writer.WriteString("lastInternalMode", ModeName(window.LastInternalMode));
        writer.WriteBoolean("isOpen", window.IsOpen);
        writer.WriteBoolean("isIconVisible", window.IsIconVisible);
        writer.WriteNumber("pairRatio", window.PairRatio);
        if (window.FloatingBounds is { } bounds)
        {
            writer.WritePropertyName("floatingBounds");
            WriteBounds(writer, bounds);
        }
        else
        {
            writer.WriteNull("floatingBounds");
        }

        writer.WritePropertyName("contentTree");
        WriteNode(writer, window.ContentTree);
        writer.WriteEndObject();
    }

    private static void WriteSide(Utf8JsonWriter writer, string name, SideState side)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("weight", side.Weight);
        writer.WriteEndObject();
    }

    private static void WriteBounds(Utf8JsonWriter writer, FloatingBounds bounds)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", bounds.X);
        writer.WriteNumber("y", bounds.Y);
        writer.WriteNumber("width", bounds.Width);
        writer.WriteNumber("height", bounds.Height);
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, TabTreeNode node)
    {
        writer.WriteStartObject();
        switch (node)
        {
            case TabGroupNode group:
                writer.WriteString("type", "group");
                writer.WriteStartArray("tabs");
                foreach (var tab in group.Tabs)
                {
                    writer.WriteStringValue(tab);
                }

                writer.WriteEndArray();
                WriteNullableString(writer, "activeTabId", group.ActiveTabId);
                break;

            case SplitNode split:
                writer.WriteString("type", "split");
                writer.WriteString("orientation", split.Orientation == SplitOrientation.Row ? "row" : "column");
                writer.WriteStartArray("children");
                foreach (var child in split.Children)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("share", child.Share);
                    writer.WritePropertyName("node");
                    WriteNode(writer, child.Node);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string SideName(ToolWindowSide side) => side switch
    {
        ToolWindowSide.Left => "left",
        ToolWindowSide.Right => "right",
        ToolWindowSide.Bottom => "bottom",
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, message: null),
    };

    private static string ModeName(ToolWindowMode mode) => mode switch
    {
        ToolWindowMode.DockPinned => "dockPinned",
        ToolWindowMode.DockUnpinned => "dockUnpinned",
        ToolWindowMode.Undock => "undock",
        ToolWindowMode.Float => "float",
        ToolWindowMode.Window => "window",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, message: null),
    };

    // ---- reading ----

    private static int ReadSchemaVersion(JsonElement root)
    {
        if (!TryGetElement(root, "schemaVersion", out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var version))
        {
            throw new LayoutFormatException("The layout document has no integer 'schemaVersion'.");
        }

        return version;
    }

    private static LayoutState ReadLayout(JsonElement root)
    {
        var state = LayoutState.Empty;
        if (TryGetElement(root, "toolWindows", out var toolWindows))
        {
            RequireKind(toolWindows, JsonValueKind.Array, "'toolWindows' must be an array.");
            var windows = ImmutableArray.CreateBuilder<ToolWindowState>(toolWindows.GetArrayLength());
            foreach (var item in toolWindows.EnumerateArray())
            {
                windows.Add(ReadToolWindow(item));
            }

            state = state with { ToolWindows = windows.ToImmutable() };
        }

        if (TryGetElement(root, "sides", out var sides))
        {
            RequireKind(sides, JsonValueKind.Object, "'sides' must be an object.");
            state = state with
            {
                Left = ReadSide(sides, "left"),
                Right = ReadSide(sides, "right"),
                Bottom = ReadSide(sides, "bottom"),
            };
        }

        state = state with
        {
            QuickAccessSide = ReadEnum(root, "quickAccessSide", QuickAccessValues, QuickAccessSide.Left),
            ActiveToolWindowId = ReadReference(root, "activeToolWindowId"),
        };

        if (TryGetElement(root, "dockArea", out var dockArea))
        {
            state = state with { DockArea = ReadDockArea(dockArea) };
        }

        return state;
    }

    private static ToolWindowState ReadToolWindow(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Object, "A tool window entry must be an object.");
        var id = ReadEntityId(element, "id", "a tool window");
        var slot = new ToolWindowSlot(
            ReadEnum(element, "side", SideValues, ToolWindowSide.Left),
            ReadEnum(element, "group", GroupValues, ToolWindowGroup.Primary));
        return new ToolWindowState(id, slot, 0) with
        {
            Order = ReadLenientOrder(element),
            Mode = ReadEnum(element, "mode", ModeValues, ToolWindowMode.DockPinned),
            LastInternalMode = ReadEnum(element, "lastInternalMode", ModeValues, LayoutDefaults.LastInternalMode),
            IsOpen = ReadBool(element, "isOpen", defaultValue: false),
            IsIconVisible = ReadBool(element, "isIconVisible", defaultValue: true),
            PairRatio = ReadLenientNumber(element, "pairRatio", LayoutDefaults.PairRatio),
            FloatingBounds = ReadOptionalBounds(element),
            ContentTree = TryGetElement(element, "contentTree", out var contentTree)
                ? ReadNode(contentTree)
                : TabGroupNode.Empty,
        };
    }

    private static SideState ReadSide(JsonElement sides, string name)
    {
        if (!TryGetElement(sides, name, out var element))
        {
            return new SideState();
        }

        RequireKind(element, JsonValueKind.Object, $"The '{name}' side must be an object.");
        return new SideState(ReadLenientNumber(element, "weight", LayoutDefaults.SideWeight));
    }

    private static DockAreaState ReadDockArea(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Object, "'dockArea' must be an object.");
        var area = DockAreaState.Empty;
        if (TryGetElement(element, "root", out var rootElement))
        {
            area = area with { Root = ReadNode(rootElement) };
        }

        area = area with { CurrentTabId = ReadReference(element, "currentTabId") };
        if (TryGetElement(element, "windows", out var windowsElement))
        {
            RequireKind(windowsElement, JsonValueKind.Array, "'windows' of the dock area must be an array.");
            var windows = ImmutableArray.CreateBuilder<DocumentWindowState>(windowsElement.GetArrayLength());
            var index = 0;
            foreach (var item in windowsElement.EnumerateArray())
            {
                windows.Add(ReadDocumentWindow(item, index));
                index++;
            }

            area = area with { Windows = windows.ToImmutable() };
        }

        return area with { ActiveDockHost = ReadActiveDockHost(element) };
    }

    private static DocumentWindowState ReadDocumentWindow(JsonElement element, int index)
    {
        RequireKind(element, JsonValueKind.Object, $"Document window {index} must be an object.");
        var bounds = ReadRequiredBounds(element, index);
        var root = TryGetElement(element, "root", out var rootElement)
            ? ReadNode(rootElement)
            : TabGroupNode.Empty;

        // The current tab of a document window is mandatory (INV-D4): the model admits no
        // window without one, so a missing, null or empty value is outside the domain
        // (TW-10.5); a dangling non-empty id is representable and healed with a report.
        if (!TryGetElement(element, "currentTabId", out var current)
            || current.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(current.GetString()))
        {
            throw new LayoutFormatException($"Document window {index} has no non-empty 'currentTabId'.");
        }

        return new DocumentWindowState(bounds, root, current.GetString()!);
    }

    private static FloatingBounds ReadRequiredBounds(JsonElement element, int index)
    {
        // Bounds of a document window are mandatory and not nullable: a missing or non-numeric
        // component has no representation — NaN would be unserializable and has no healing
        // rule here, unlike the nullable bounds of a tool window (TW-10.5, DA-9.5).
        if (!TryGetElement(element, "bounds", out var bounds) || bounds.ValueKind != JsonValueKind.Object)
        {
            throw new LayoutFormatException($"Document window {index} has no 'bounds' object.");
        }

        return new FloatingBounds(
            ReadStrictNumber(bounds, "x", index),
            ReadStrictNumber(bounds, "y", index),
            ReadStrictNumber(bounds, "width", index),
            ReadStrictNumber(bounds, "height", index));
    }

    private static double ReadStrictNumber(JsonElement obj, string name, int index)
    {
        if (!TryGetElement(obj, name, out var element) || element.ValueKind != JsonValueKind.Number)
        {
            throw new LayoutFormatException($"Bounds component '{name}' of document window {index} must be a number.");
        }

        return element.GetDouble();
    }

    private static DockHost ReadActiveDockHost(JsonElement element)
    {
        if (!TryGetElement(element, "activeDockHost", out var host))
        {
            return DockHost.MainWindow;
        }

        if (host.ValueKind != JsonValueKind.Number || !host.TryGetInt32(out var index) || index < 0)
        {
            throw new LayoutFormatException(
                $"'activeDockHost' value {host.GetRawText()} is outside the model domain: null or a non-negative window index.");
        }

        // An index beyond the window list is representable and repaired with a report (N5).
        return DockHost.DocumentWindow(index);
    }

    private static TabTreeNode ReadNode(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Object, "A tree node must be an object.");
        if (!TryGetElement(element, "type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            throw new LayoutFormatException("A tree node must have a string 'type'.");
        }

        return type.GetString() switch
        {
            "group" => ReadGroup(element),
            "split" => ReadSplit(element),
            var unknown => throw new LayoutFormatException(
                $"Tree node type '{unknown}' is outside the model domain."),
        };
    }

    private static TabGroupNode ReadGroup(JsonElement element)
    {
        var tabs = ImmutableArray<string>.Empty;
        if (TryGetElement(element, "tabs", out var tabsElement))
        {
            RequireKind(tabsElement, JsonValueKind.Array, "'tabs' must be an array.");
            var builder = ImmutableArray.CreateBuilder<string>(tabsElement.GetArrayLength());
            foreach (var tab in tabsElement.EnumerateArray())
            {
                if (tab.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tab.GetString()))
                {
                    throw new LayoutFormatException("A tab id must be a non-empty string.");
                }

                builder.Add(tab.GetString()!);
            }

            tabs = builder.ToImmutable();
        }

        return new TabGroupNode { Tabs = tabs, ActiveTabId = ReadReference(element, "activeTabId") };
    }

    private static SplitNode ReadSplit(JsonElement element)
    {
        var orientation = ReadEnum(element, "orientation", OrientationValues, SplitOrientation.Row);
        var children = ImmutableArray<SplitChild>.Empty;
        if (TryGetElement(element, "children", out var childrenElement))
        {
            RequireKind(childrenElement, JsonValueKind.Array, "'children' must be an array.");
            var builder = ImmutableArray.CreateBuilder<SplitChild>(childrenElement.GetArrayLength());
            foreach (var child in childrenElement.EnumerateArray())
            {
                RequireKind(child, JsonValueKind.Object, "A split child must be an object.");
                if (!TryGetElement(child, "node", out var nodeElement))
                {
                    throw new LayoutFormatException("A split child must have a 'node'.");
                }

                // A missing share has no safe default (it depends on the siblings): NaN sends
                // the whole vector to the N4 repair, same as a non-numeric value.
                builder.Add(new SplitChild(ReadNode(nodeElement), ReadLenientNumber(child, "share", double.NaN)));
            }

            children = builder.ToImmutable();
        }

        return new SplitNode { Orientation = orientation, Children = children };
    }

    private static string ReadEntityId(JsonElement obj, string name, string what)
    {
        if (!TryGetElement(obj, name, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new LayoutFormatException($"The '{name}' of {what} must be a non-empty string.");
        }

        return element.GetString()!;
    }

    /// <summary>A nullable reference field: absent or null → null; a dangling value is representable and healed by Apply, but a non-string value is outside the domain.</summary>
    private static string? ReadReference(JsonElement obj, string name)
    {
        if (!TryGetElement(obj, name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new LayoutFormatException($"'{name}' must be a string or null.");
        }

        return element.GetString();
    }

    private static T ReadEnum<T>(JsonElement obj, string name, Dictionary<string, T> values, T defaultValue)
    {
        if (!TryGetElement(obj, name, out var element))
        {
            return defaultValue;
        }

        if (element.ValueKind == JsonValueKind.String && values.TryGetValue(element.GetString()!, out var value))
        {
            return value;
        }

        throw new LayoutFormatException($"'{name}' value {element.GetRawText()} is outside the model domain.");
    }

    private static bool ReadBool(JsonElement obj, string name, bool defaultValue)
    {
        if (!TryGetElement(obj, name, out var element))
        {
            return defaultValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new LayoutFormatException(
                $"'{name}' value {element.GetRawText()} is outside the model domain: expected a boolean."),
        };
    }

    /// <summary>A numeric field with a healing rule: absent → the default, a non-numeric value → NaN, repaired by Apply with a report (TW-10.5, DA-E23).</summary>
    private static double ReadLenientNumber(JsonElement obj, string name, double defaultValue)
    {
        if (!TryGetElement(obj, name, out var element))
        {
            return defaultValue;
        }

        return element.ValueKind == JsonValueKind.Number ? element.GetDouble() : double.NaN;
    }

    /// <summary>The order: absent → 0; a non-integer value → −1, the representable invalid order, compacted by Apply with a report (INV-3).</summary>
    private static int ReadLenientOrder(JsonElement obj)
    {
        if (!TryGetElement(obj, "order", out var element))
        {
            return 0;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var order) ? order : -1;
    }

    /// <summary>Nullable bounds of a tool window: absent or null → null; garbage components → NaN, reset to null by Apply with a report (TW-10.4).</summary>
    private static FloatingBounds? ReadOptionalBounds(JsonElement obj)
    {
        if (!TryGetElement(obj, "floatingBounds", out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return new FloatingBounds(double.NaN, double.NaN, double.NaN, double.NaN);
        }

        return new FloatingBounds(
            ReadLenientNumber(element, "x", double.NaN),
            ReadLenientNumber(element, "y", double.NaN),
            ReadLenientNumber(element, "width", double.NaN),
            ReadLenientNumber(element, "height", double.NaN));
    }

    /// <summary>Gets a property treating JSON null as absence (TW-10.5: null is equivalent to a missing field).</summary>
    private static bool TryGetElement(JsonElement obj, string name, out JsonElement element)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out element)
            && element.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        element = default;
        return false;
    }

    private static void RequireKind(JsonElement element, JsonValueKind kind, string message)
    {
        if (element.ValueKind != kind)
        {
            throw new LayoutFormatException(message);
        }
    }
}
