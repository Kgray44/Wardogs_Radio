using WardogsRadio.Input;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class GlobalHotkeyServiceTests
{
    [Fact]
    public void OtherWindowMessagesNeverConvertLargeWparamToInt32()
    {
        using var service = new GlobalHotkeyService(IntPtr.Zero);
        Assert.False(service.IsHotkeyMessage(0x00ff, new IntPtr(long.MaxValue), out _));
        Assert.False(service.IsHotkeyMessage(0x0312, new IntPtr(long.MaxValue), out _));
    }
}
