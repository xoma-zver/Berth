namespace Berth;

/// <summary>
/// Reference to a host of the dock area: the main window or one of the document windows.
/// Document windows are identified by their index in <see cref="DockAreaState.Windows"/>;
/// the default value is the main window.
/// </summary>
public readonly record struct DockHost
{
    private readonly int _windowIndexPlusOne;

    private DockHost(int windowIndexPlusOne) => _windowIndexPlusOne = windowIndexPlusOne;

    /// <summary>The main window host.</summary>
    public static DockHost MainWindow => default;

    /// <summary>The document window at <paramref name="index"/> in <see cref="DockAreaState.Windows"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is negative.</exception>
    public static DockHost DocumentWindow(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return new DockHost(index + 1);
    }

    /// <summary>Index of the document window in <see cref="DockAreaState.Windows"/>, or null for the main window.</summary>
    public int? DocumentWindowIndex => _windowIndexPlusOne == 0 ? null : _windowIndexPlusOne - 1;

    /// <summary>Whether this reference points at the main window.</summary>
    public bool IsMainWindow => _windowIndexPlusOne == 0;
}
