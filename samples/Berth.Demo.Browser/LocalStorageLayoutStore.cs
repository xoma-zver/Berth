using System.Runtime.InteropServices.JavaScript;
using Berth.Demo;

namespace Berth.Demo.Browser;

/// <summary>
/// Layout store of the browser host: the browser sandbox has no real file system, and its
/// counterpart of the layout file is web storage — localStorage, a synchronous per-origin
/// key-value store surviving restarts, ideal for one JSON document (task 7.0, owner decision).
/// Bounds stored here are workspace coordinates (TW-7.7); a layout carried over from the
/// desktop is healed by the overlay validator on restore (TW-7.4).
/// </summary>
internal sealed partial class LocalStorageLayoutStore : ILayoutStore
{
    private const string Key = "berth-demo.layout";

    /// <inheritdoc/>
    public string? Load() => GetItem(Key);

    /// <inheritdoc/>
    public void Save(string json) => SetItem(Key, json);

    [JSImport("globalThis.localStorage.getItem")]
    private static partial string? GetItem(string key);

    [JSImport("globalThis.localStorage.setItem")]
    private static partial void SetItem(string key, string value);
}
