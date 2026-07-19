namespace Berth;

/// <summary>
/// Direction of a split operation: <see cref="Left"/>/<see cref="Right"/> produce
/// or extend a <see cref="SplitOrientation.Row"/>, <see cref="Up"/>/<see cref="Down"/> —
/// a <see cref="SplitOrientation.Column"/>; <see cref="Left"/>/<see cref="Up"/> place the new
/// group before the split one, <see cref="Right"/>/<see cref="Down"/> — after.
/// </summary>
public enum SplitDirection
{
    /// <summary>New group to the left of the split one (a Row, inserted before).</summary>
    Left,

    /// <summary>New group to the right of the split one (a Row, inserted after).</summary>
    Right,

    /// <summary>New group above the split one (a Column, inserted before).</summary>
    Up,

    /// <summary>New group below the split one (a Column, inserted after).</summary>
    Down,
}
