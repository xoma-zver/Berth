namespace Berth;

/// <summary>
/// Failure to load a persisted layout document: the document is not parseable JSON, its root
/// or schema version is unsupported, or a value lies outside the domain of the typed model —
/// an unknown enum member, an empty entity id, a non-boolean flag, a missing mandatory part
/// such as the current tab or a bounds component of a document window. Value-level defects the
/// model can represent — a non-numeric number (→ NaN), a dangling reference, a negative
/// order — do not raise this error: they load as-is and are repaired by
/// <see cref="LayoutApply.Apply"/> with a report. The caller handles the error explicitly; the
/// typical reaction is <see cref="LayoutApply.ResetToDefaults"/>.
/// </summary>
public sealed class LayoutFormatException : Exception
{
    /// <summary>Creates the exception without a message.</summary>
    public LayoutFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public LayoutFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public LayoutFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
