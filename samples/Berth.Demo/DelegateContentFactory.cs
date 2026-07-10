using System;

namespace Berth.Demo;

/// <summary>
/// Minimal factory adapter for the demo panels: creation by delegate; demo content holds no
/// resources, so releasing simply drops the object.
/// </summary>
internal sealed class DelegateContentFactory(Func<string, object> create) : IToolWindowContentFactory
{
    public object CreateContent(string toolWindowId) => create(toolWindowId);

    public void ReleaseContent(string toolWindowId, object content)
    {
    }
}
