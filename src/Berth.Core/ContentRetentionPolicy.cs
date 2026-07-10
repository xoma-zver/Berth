namespace Berth;

/// <summary>Retention axis of the content lifecycle policy (spec TW-9.2).</summary>
public enum ContentRetentionPolicy
{
    /// <summary>Content lives until the tool window is unregistered (the default).</summary>
    KeepWhileRegistered,

    /// <summary>
    /// Content is released on every transition of the tool window out of the open state — by any
    /// path: Close, eviction (spec TW-5.1), icon hiding (TW-5.10), HideAll (TW-5.12) or a layout
    /// apply (TW-10.7) — and is recreated by the next materialization. Content created by
    /// <see cref="ContentCreationPolicy.Eager"/> for a closed window has seen no such transition
    /// and lives until the first one, or until unregistration (spec TW-9.2, TW-9.4).
    /// </summary>
    DisposeOnClose,
}
