using WardogsRadio.Input;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class ControllerEventTests
{
    [Fact]
    public void BindingCodecRoundTripsControllerIdentity()
    {
        var encoded = ControllerBindingCodec.Encode(@"\\?\HID#VID_231D&PID_0201#7&34", "pov-2");
        Assert.True(ControllerBindingCodec.TryDecode(encoded, out var device, out var control));
        Assert.Equal(@"\\?\HID#VID_231D&PID_0201#7&34", device);
        Assert.Equal("pov-2", control);
        Assert.False(ControllerBindingCodec.TryDecode("controller:bad!:button-1", out _, out _));
    }

    [Fact]
    public void EdgeTrackerOnlyEmitsChangesAndReleasesOnDisconnect()
    {
        var edges = new ControllerEdgeTracker();
        Assert.Single(edges.Update("device", "HOTAS", [("button-17", "Button 17")]));
        Assert.Empty(edges.Update("device", "HOTAS", [("button-17", "Button 17")]));
        var changed = edges.Update("device", "HOTAS", [("pov-2", "POV East")]);
        Assert.Equal(2, changed.Count);
        Assert.Contains(changed, x => x.ControlId == "button-17" && !x.Pressed);
        Assert.Contains(changed, x => x.ControlId == "pov-2" && x.Pressed);
        Assert.Single(edges.ReleaseAll("device", "HOTAS"));
    }
}
