namespace Berth.Demo;

/// <summary>
/// Storage seam of the application-side layout persistence (TW-10.1; ADR-0003: the file —
/// or any other store — is the application's concern, the core only serializes). The desktop
/// host supplies a file store, the browser host — a localStorage one; a host without a store
/// simply starts from defaults. The document format and its versioning belong to the core
/// (<see cref="LayoutPersistence"/>); the store moves opaque text.
/// </summary>
public interface ILayoutStore
{
    /// <summary>The stored layout document, or null when nothing was stored yet (or the store is unreadable).</summary>
    public string? Load();

    /// <summary>Stores the layout document, replacing the previous one.</summary>
    public void Save(string json);
}
