using WardogsRadio.Playback;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class AudioSamplePeakTests
{
    [Fact]
    public void ReadsAbsoluteFloatPeak()
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(.2f).CopyTo(bytes, 0);
        BitConverter.GetBytes(-.7f).CopyTo(bytes, 4);
        BitConverter.GetBytes(float.NaN).CopyTo(bytes, 8);
        BitConverter.GetBytes(.4f).CopyTo(bytes, 12);
        Assert.Equal(.7f, AudioSamplePeak.Float32(bytes));
    }

    [Fact]
    public void SilentAndIncompleteSamplesDoNotReadPastBuffer()
    {
        Assert.Equal(0, AudioSamplePeak.Float32([]));
        Assert.Equal(0, AudioSamplePeak.Float32([1, 2, 3]));
    }
}
