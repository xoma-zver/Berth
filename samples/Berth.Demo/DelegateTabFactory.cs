using System;

namespace Berth.Demo;

/// <summary>
/// Minimal dock content factory for the demo documents: ownership by prefix predicate (spec
/// TW-9.11), creation by delegate; demo content holds no resources, so releasing simply drops
/// the object.
/// </summary>
internal sealed class DelegateTabFactory(Func<string, bool> owns, Func<string, object?> create) : ITabContentFactory
{
    public bool OwnsTab(string tabId) => owns(tabId);

    public object? CreateContent(string tabId) => create(tabId);

    public void ReleaseContent(string tabId, object content)
    {
    }
}
