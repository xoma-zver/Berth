using Xunit;

namespace Berth.Core.Tests;

/// <summary>Dock-area state model: defaults of DA-2.5, host references, constructor guards.</summary>
public class DockAreaStateTests
{
    [Fact]
    public void DA_2_5_empty_layout_has_an_empty_main_tree_and_no_windows()
    {
        var dock = LayoutState.Empty.DockArea;

        var root = Assert.IsType<TabGroupNode>(dock.Root);
        Assert.True(root.Tabs.IsEmpty);
        Assert.Null(root.ActiveTabId);
        Assert.Null(dock.CurrentTabId);
        Assert.True(dock.Windows.IsEmpty);
        Assert.True(dock.ActiveDockHost.IsMainWindow);
    }

    [Fact]
    public void DockHost_default_is_the_main_window()
    {
        Assert.Equal(DockHost.MainWindow, default(DockHost));
        Assert.True(DockHost.MainWindow.IsMainWindow);
        Assert.Null(DockHost.MainWindow.DocumentWindowIndex);
    }

    [Fact]
    public void DockHost_document_window_keeps_its_index()
    {
        var host = DockHost.DocumentWindow(2);

        Assert.False(host.IsMainWindow);
        Assert.Equal(2, host.DocumentWindowIndex);
        Assert.Equal(DockHost.DocumentWindow(2), host);
        Assert.NotEqual(DockHost.DocumentWindow(1), host);
        Assert.NotEqual(DockHost.MainWindow, host);
    }

    [Fact]
    public void DockHost_negative_index_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DockHost.DocumentWindow(-1));
    }

    [Fact]
    public void Document_window_state_requires_a_root_and_a_current_tab()
    {
        var bounds = new FloatingBounds(0, 0, 100, 100);

        Assert.Throws<ArgumentNullException>(() => new DocumentWindowState(bounds, null!, "x"));
        Assert.Throws<ArgumentException>(() => new DocumentWindowState(bounds, TabGroupNode.Empty, ""));
        Assert.Throws<ArgumentException>(() => new DocumentWindowState(bounds, TabGroupNode.Empty, "  "));
    }

    [Fact]
    public void INV_D3_share_sum_tolerance_is_fixed()
    {
        // The numeric tolerance is a core constant fixed by tests (INV-D3).
        Assert.Equal(1e-9, TabTreeNormalization.ShareSumTolerance);
    }
}
