using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Result of <see cref="LayoutApply.Apply"/>: the normalized state plus the report of applied
/// corrections. An application wanting «a proper layout or nothing» builds its strict mode on
/// top of the report — apply, then reject the result when <see cref="Fixes"/> is non-empty;
/// strictness is an application policy, not a core one.
/// </summary>
/// <param name="State">The normalized layout; satisfies every invariant of both specs.</param>
/// <param name="Fixes">Corrections applied on the way; empty for a defect-free snapshot.</param>
public sealed record ApplyResult(LayoutState State, ImmutableArray<AppliedFix> Fixes);
