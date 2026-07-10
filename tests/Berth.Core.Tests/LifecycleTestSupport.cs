namespace Berth.Core.Tests;

/// <summary>
/// Counting factory stub for tool window body content. Verifies the release contract: released
/// content must be a live object previously created by this factory, passed back exactly once.
/// </summary>
internal sealed class StubToolWindowFactory : IToolWindowContentFactory
{
    private readonly HashSet<object> _live = [];

    public int Created { get; private set; }

    public int Released { get; private set; }

    public bool ThrowOnCreate { get; set; }

    public int LiveCount => _live.Count;

    public object CreateContent(string toolWindowId)
    {
        if (ThrowOnCreate)
        {
            throw new InvalidOperationException($"Factory failure for '{toolWindowId}'.");
        }

        Created++;
        var content = new object();
        _live.Add(content);
        return content;
    }

    public void ReleaseContent(string toolWindowId, object content)
    {
        if (!_live.Remove(content))
        {
            throw new InvalidOperationException(
                $"Released content of '{toolWindowId}' is not a live object of this factory.");
        }

        Released++;
    }
}

/// <summary>
/// Counting factory stub for tab content, claiming ids by prefix (spec TW-9.11). A refusal
/// predicate models spec DA-9.3; the release contract mirrors <see cref="StubToolWindowFactory"/>.
/// </summary>
internal sealed class StubTabFactory(string prefix) : ITabContentFactory
{
    private readonly HashSet<object> _live = [];

    public int Created { get; private set; }

    public int Released { get; private set; }

    public Func<string, bool>? Refuse { get; set; }

    public int LiveCount => _live.Count;

    public bool OwnsTab(string tabId) => tabId.StartsWith(prefix, StringComparison.Ordinal);

    public object? CreateContent(string tabId)
    {
        if (Refuse?.Invoke(tabId) == true)
        {
            return null;
        }

        Created++;
        var content = new object();
        _live.Add(content);
        return content;
    }

    public void ReleaseContent(string tabId, object content)
    {
        if (!_live.Remove(content))
        {
            throw new InvalidOperationException(
                $"Released content of '{tabId}' is not a live object of this factory.");
        }

        Released++;
    }
}
