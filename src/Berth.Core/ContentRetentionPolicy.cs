namespace Berth;

/// <summary>Retention axis of the content lifecycle policy.</summary>
public enum ContentRetentionPolicy
{
    /// <summary>Content lives until the tool window is unregistered (the default).</summary>
    KeepWhileRegistered,

    /// <summary>
    /// Content is released on every transition of the tool window out of the open state — by
    /// any path: Close, eviction, icon hiding, HideAll or a layout apply — and is recreated by
    /// the next materialization. Content created by <see cref="ContentCreationPolicy.Eager"/>
    /// for a closed window has seen no such transition and lives until the first one, or until
    /// unregistration.
    /// </summary>
    DisposeOnClose,
}
