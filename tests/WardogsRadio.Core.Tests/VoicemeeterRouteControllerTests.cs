using WardogsRadio.Core;
using WardogsRadio.Voicemeeter;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class VoicemeeterRouteControllerTests
{
    sealed class Remote : IVoicemeeterRemote
    {
        public Dictionary<string, float> Parameters { get; } = [];
        public bool AllowWrites = true;
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
    public void RouteActionRestoresTheExactPreviousState()
    {
        var remote = new Remote();
        remote.Parameters["Strip[3].B1"] = 0;
        var routes = new VoicemeeterRouteController(remote);
        var context = new MacroExecutionContext();
        Assert.Contains("enabled", routes.Change(3, "B1", true, context));
        Assert.Equal(1, remote.Parameters["Strip[3].B1"]);
        Assert.Contains("off", routes.Restore(3, "B1", context));
        Assert.Equal(0, remote.Parameters["Strip[3].B1"]);
    }

    [Fact]
    public void FailedRestoreKeepsThePriorStateForRetry()
    {
        var remote = new Remote();
        remote.Parameters["Strip[3].A1"] = 1;
        var routes = new VoicemeeterRouteController(remote);
        var context = new MacroExecutionContext();
        routes.Change(3, "A1", false, context);
        remote.AllowWrites = false;
        Assert.Throws<InvalidOperationException>(() => routes.Restore(3, "A1", context));
        remote.AllowWrites = true;
        routes.Restore(3, "A1", context);
        Assert.Equal(1, remote.Parameters["Strip[3].A1"]);
    }

    [Fact]
    public void NoConfiguredRouteIsNeverWritten()
    {
        var remote = new Remote();
        var routes = new VoicemeeterRouteController(remote);
        Assert.Throws<InvalidOperationException>(() => routes.Change(-1, "B1", true, new MacroExecutionContext()));
        Assert.False(routes.TryRead(0, "A4", out _));
        Assert.Empty(remote.Parameters);
    }

    [Fact]
    public void RestoreLeavesExternallyChangedRouteUntouched()
    {
        var remote = new Remote();
        remote.Parameters["Strip[4].B1"] = 0;
        var routes = new VoicemeeterRouteController(remote);
        var context = new MacroExecutionContext();
        routes.Change(4, "B1", true, context);
        remote.Parameters["Strip[4].B1"] = .4f;
        Assert.Throws<InvalidOperationException>(() => routes.Restore(4, "B1", context));
        Assert.Equal(.4f, remote.Parameters["Strip[4].B1"]);
    }
}
