namespace Berth;

/// <summary>
/// Node of a tab-group tree — the single tree model shared by the dock area of the main window,
/// document windows and tool window content (spec DA-1.1, TW-9.5). A closed hierarchy of exactly
/// two kinds: <see cref="SplitNode"/> and <see cref="TabGroupNode"/> (ADR-0005).
/// </summary>
public abstract record TabTreeNode
{
    private protected TabTreeNode()
    {
    }
}
