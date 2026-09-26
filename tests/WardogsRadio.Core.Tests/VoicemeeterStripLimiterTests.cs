using WardogsRadio.Voicemeeter;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class VoicemeeterStripLimiterTests
{
    sealed class Remote : IVoicemeeterRemote
    {
        public Dictionary<string, float> Parameters { get; } = [];
        public bool AllowWrites { get; set; } = true;
        public VoicemeeterStatus Probe() => new(true, true, "Banana", null, "test", "Connected") { Connected = true };
        public bool TryLogin(out string detail) { detail = "Connected"; return true; }
        public bool TryGetLevel(int type, int channel, out float value) { value = 0; return false; }
        public bool TryGetParameterFloat(string name, out float value) => Parameters.TryGetValue(name, out value);
        public bool TrySetParameterFloat(string name, float value)
        {
            if (!AllowWrites || !Parameters.ContainsKey(name)) return false;
            Parameters[name] = value;
            return true;
        }
        public void Dispose() { }
    }

    [Fact]
    public void CapabilityReadDoesNotWriteTheMixer()
    {
        var remote = new Remote();
        remote.Parameters["Strip[4].Limit"] = 4;
        var limiter = new VoicemeeterStripLimiter(remote);

        var capability = limiter.Probe(4);

        Assert.True(capability.Available);
        Assert.Equal(4, remote.Parameters["Strip[4].Limit"]);
        Assert.False(limiter.IsActive);
    }

    [Fact]
    public void ApplyVerifiesAndRestoresTheExactPriorLimiterValue()
    {
        var remote = new Remote();
        remote.Parameters["Strip[3].Limit"] = 2.5f;
        var limiter = new VoicemeeterStripLimiter(remote);

        Assert.True(limiter.TryApply(3, -3, out _));
        Assert.True(limiter.IsActive);
        Assert.Equal(-3, remote.Parameters["Strip[3].Limit"]);

        Assert.True(limiter.TryRestore(out _));
        Assert.False(limiter.IsActive);
        Assert.Equal(2.5f, remote.Parameters["Strip[3].Limit"]);
    }

    [Fact]
    public void FailedWriteNeverClaimsThatTheLimiterIsActive()
    {
        var remote = new Remote { AllowWrites = false };
        remote.Parameters["Strip[3].Limit"] = 1;
        var limiter = new VoicemeeterStripLimiter(remote);

        Assert.False(limiter.TryApply(3, -3, out var detail));
        Assert.Contains("could not be verified", detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(limiter.IsActive);
        Assert.Equal(1, remote.Parameters["Strip[3].Limit"]);
    }
}
