namespace Berth;

/// <summary>Creation axis of the content lifecycle policy (spec TW-9.2).</summary>
public enum ContentCreationPolicy
{
    /// <summary>Content is created lazily, on the first materialization request (the default).</summary>
    OnFirstOpen,

    /// <summary>Content is created at registration, regardless of the tool window's openness (spec TW-9.2).</summary>
    Eager,
}
