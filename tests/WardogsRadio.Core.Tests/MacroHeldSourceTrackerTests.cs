using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class MacroHeldSourceTrackerTests
{
    [Fact]
    public void TwoBindingsKeepMacroHeldUntilLastRelease()
    {
        var id = Guid.NewGuid();
        var tracker = new MacroHeldSourceTracker();
        Assert.True(tracker.Press(id, "hotkey:1"));
        Assert.False(tracker.Press(id, "controller:button-22"));
        Assert.False(tracker.Release(id, "hotkey:1"));
        Assert.True(tracker.HasSource(id, "controller:button-22"));
        Assert.True(tracker.Release(id, "controller:button-22"));
        Assert.True(tracker.Press(id, "hotkey:1"));
    }

    [Fact]
    public void DuplicatePressAndUnknownReleaseDoNotChangeState()
    {
        var id = Guid.NewGuid();
        var tracker = new MacroHeldSourceTracker();
        Assert.True(tracker.Press(id, "F8"));
        Assert.False(tracker.Press(id, "f8"));
        Assert.False(tracker.Release(id, "other"));
        Assert.True(tracker.HasSource(id, "F8"));
        Assert.True(tracker.Release(id, "f8"));
        Assert.False(tracker.Release(id, "f8"));
    }
}
