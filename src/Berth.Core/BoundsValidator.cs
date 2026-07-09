namespace Berth;

/// <summary>
/// UI-supplied validation of saved screen bounds at <see cref="LayoutApply.Apply"/> (spec
/// TW-7.4, DA-7.4). The core knows nothing about screens and never invents pixels (ADR-0002):
/// the UI decides whether saved bounds are still sufficiently visible and computes the
/// replacement — typically positioned relative to the main window with a default size. Without
/// a validator (headless, tests) saved bounds pass through as-is.
/// </summary>
/// <param name="saved">Saved screen bounds of a floating tool window or a document window.</param>
/// <returns>Replacement bounds, or null when <paramref name="saved"/> is valid.</returns>
public delegate FloatingBounds? BoundsValidator(FloatingBounds saved);
