namespace Berth;

/// <summary>
/// One correction applied by <see cref="LayoutApply.Apply"/>. Every fix is reported exactly
/// once; secondary activity reassignments caused by a fix — a host's current tab after
/// deduplication, the active dock host after a window removal — produce no entries of their
/// own. Regular behaviour (eviction, reconciliation, the Arrangement reset of the active tool
/// window) is not a fix and is never reported.
/// </summary>
/// <param name="Rule">
/// Spec rule behind the fix: the invariant id ("INV-2", "INV-D3", …) for defects found by
/// validation of the incoming snapshot, or the rule id for fixes emitted by the apply pipeline
/// itself — "DA-9.2" for tab deduplication, "INV-D6" for the removal of an emptied document
/// window, "TW-7.4"/"DA-7.4" for bounds replaced by the UI validator, "TW-10.4" for
/// non-numeric bounds reset to null.
/// </param>
/// <param name="SubjectId">Id of the tool window or tab the fix concerns, or null for layout-level fixes.</param>
/// <param name="Message">Human-readable description of the defect and the applied fix.</param>
public sealed record AppliedFix(string Rule, string? SubjectId, string Message);
