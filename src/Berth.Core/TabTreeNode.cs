namespace Berth;

/// <summary>
/// Node of a tab-group tree — the single tree model shared by the dock area of the main window,
/// document windows and tool window content. A closed hierarchy of exactly two kinds:
/// <see cref="SplitNode"/> and <see cref="TabGroupNode"/>.
/// </summary>
public abstract record TabTreeNode
{
    private protected TabTreeNode()
    {
    }
}
