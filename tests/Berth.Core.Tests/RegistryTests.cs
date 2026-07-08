using Xunit;

namespace Berth.Core.Tests;

/// <summary>Registration rules of the descriptor registry (INV-1, spec TW-9.1).</summary>
public class RegistryTests
{
    private static readonly ToolWindowSlot Slot = new(ToolWindowSide.Left, ToolWindowGroup.Primary);

    [Fact]
    public void INV_1_duplicate_id_registration_fails()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("tw", "Tool", Slot));

        Assert.Throws<ArgumentException>(() => registry.Register(new ToolWindowDescriptor("tw", "Other", Slot)));
    }

    [Fact]
    public void TW_9_1_descriptor_rejects_empty_id_and_title()
    {
        Assert.Throws<ArgumentException>(() => new ToolWindowDescriptor("", "Tool", Slot));
        Assert.Throws<ArgumentException>(() => new ToolWindowDescriptor("tw", " ", Slot));
    }

    [Fact]
    public void TW_9_1_registered_descriptor_is_retrievable()
    {
        var registry = new ToolWindowRegistry();
        var descriptor = new ToolWindowDescriptor("tw", "Tool", Slot);
        registry.Register(descriptor);

        Assert.True(registry.TryGet("tw", out var found));
        Assert.Same(descriptor, found);
        Assert.False(registry.TryGet("absent", out _));
    }
}
