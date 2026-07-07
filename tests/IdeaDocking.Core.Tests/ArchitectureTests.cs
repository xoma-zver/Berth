using System.Reflection;
using Xunit;

namespace IdeaDocking.Core.Tests;

/// <summary>Layer boundary guard (ADR-0002): the core must stay UI-framework-free.</summary>
public class ArchitectureTests
{
    [Fact]
    public void Core_does_not_reference_avalonia()
    {
        var core = Assembly.Load("IdeaDocking.Core");
        var references = core.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain(
            references,
            name => name!.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
    }
}
