using System.Buffers.Binary;
using WardogsRadio.Input;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class HidCapabilityClassifierTests
{
    [Fact]
    public void InfrastructureCapsWithoutGameControlsAreRejected()
    {
        var vendorValue = Cap(0xFF00, 1, 1);
        var caps = HidCapabilityClassifier.Parse([], 0, vendorValue, 1);
        Assert.False(caps.HasGameControls);
        Assert.Equal(0, caps.Axes);
    }

    [Fact]
    public void JoystickButtonRangeAxesAndHatAreCountedAsControls()
    {
        var buttons = Cap(0x09, 1, 18);
        var values = Cap(0x01, 0x30, 0x35);
        var hat = Cap(0x01, 0x39, 0x39);
        var bothValues = values.Concat(hat).ToArray();
        var caps = HidCapabilityClassifier.Parse(buttons, 1, bothValues, 2);
        Assert.True(caps.HasGameControls);
        Assert.Equal(18, caps.Buttons);
        Assert.Equal(6, caps.Axes);
        Assert.Equal(1, caps.Povs);
    }

    [Fact]
    public void AliasDoesNotDoubleCount()
    {
        var button = Cap(0x09, 1, 2);
        var alias = Cap(0x09, 1, 2);
        alias[3] = 1;
        var caps = HidCapabilityClassifier.Parse(button.Concat(alias).ToArray(), 2, [], 0);
        Assert.Equal(2, caps.Buttons);
    }

    static byte[] Cap(ushort page, ushort minimum, ushort maximum)
    {
        var cap = new byte[HidCapabilityClassifier.CapabilityRecordBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(cap.AsSpan(0, 2), page);
        cap[12] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(cap.AsSpan(56, 2), minimum);
        BinaryPrimitives.WriteUInt16LittleEndian(cap.AsSpan(58, 2), maximum);
        return cap;
    }
}
