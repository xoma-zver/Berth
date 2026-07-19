namespace Berth;

/// <summary>One detected violation of a layout invariant.</summary>
/// <param name="InvariantId">Spec identifier of the violated invariant, e.g. "INV-3".</param>
/// <param name="ToolWindowId">Id of the offending tool window, or null for layout-level violations.</param>
/// <param name="Message">Human-readable description of the violation.</param>
public sealed record InvariantViolation(string InvariantId, string? ToolWindowId, string Message);
