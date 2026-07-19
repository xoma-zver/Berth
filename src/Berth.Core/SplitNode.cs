using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Split node of a tab-group tree: an orientation and ordered children with their shares.
/// In a canonical tree a split has at least two children, each a group or a split of the
/// perpendicular orientation, with shares in (0..1) summing to 1; normalization repairs any
/// deviation.
/// </summary>
public sealed record SplitNode : TabTreeNode
{
    /// <summary>Layout direction of the children.</summary>
    public SplitOrientation Orientation { get; init; }

    /// <summary>Ordered children paired with their shares along the orientation.</summary>
    public ImmutableArray<SplitChild> Children { get; init; } = [];
}

/// <summary>
/// One child of a <see cref="SplitNode"/> paired with its share, so the share vector can never
/// desynchronize from the children.
/// </summary>
/// <param name="Node">The child node.</param>
/// <param name="Share">Share of the child along the split orientation, in (0..1); fractions only, never pixels.</param>
public readonly record struct SplitChild(TabTreeNode Node, double Share);
