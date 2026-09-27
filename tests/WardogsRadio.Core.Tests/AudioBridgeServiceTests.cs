using WardogsRadio.Voicemeeter;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class AudioBridgeServiceTests
{
    sealed class Remote : IVoicemeeterRemote
    {
        public bool Installed = true, Connected, Running, AllowStart = true, ApplyWrites = true;
        public string Edition = "Banana";
        public int StartCalls;
        public int ProbeCalls;
        public Dictionary<string, float> Values { get; } = [];
        public Dictionary<string, string> Strings { get; } = [];
        public VoicemeeterStatus Probe()
        {
            Interlocked.Increment(ref ProbeCalls);
            return new(Installed, Running, Connected ? Edition : null, null, "fake", "fake") { Connected = Connected };
        }
        public bool TryLogin(out string detail) { detail = "registered"; return Connected; }
        public bool TryRunVoicemeeter(int edition, out string detail)
        {
            StartCalls++;
            detail = AllowStart ? "started" : "failed";
            if (AllowStart) { Running = true; Connected = true; }
            return AllowStart;
        }
        public bool TryGetLevel(int type, int channel, out float value) { value = 0; return false; }
        public bool TryGetParameterFloat(string name, out float value) => Values.TryGetValue(name, out value);
        public bool TrySetParameterFloat(string name, float value)
        {
            if (!Values.ContainsKey(name)) return false;
            if (ApplyWrites) Values[name] = value;
            return true;
        }
        public bool TryGetParameterString(string name, out string value) => Strings.TryGetValue(name, out value!);
        public bool TrySetParameterString(string name, string value)
        {
            var readback = name[..name.LastIndexOf('.')] + ".name";
            if (!Strings.ContainsKey(readback)) return false;
            if (ApplyWrites) Strings[readback] = value;
            return true;
        }
        public IReadOnlyList<VoicemeeterAudioDevice> ListAudioDevices(bool inputs) =>
            [new(3, "Owner microphone", "device-owner"), new(3, "WARDOGS microphone", "device-wardogs")];
        public void Dispose() { }
    }

    [Fact]
    public async Task AlreadyRunningBananaDoesNotRestart()
    {
        var remote = new Remote { Connected = true, Running = true };
        var bridge = new AudioBridgeService(remote);
        Assert.True((await bridge.EnsureReadyAsync()).Success);
        Assert.Equal(0, remote.StartCalls);
        Assert.Equal(AudioBridgeConnectionState.Connected, bridge.Snapshot.Connection);
    }

    [Fact]
    public async Task StoppedBananaStartsThroughRemoteApi()
    {
        var remote = new Remote();
        var bridge = new AudioBridgeService(remote);
        Assert.True((await bridge.EnsureReadyAsync()).Success);
        Assert.Equal(1, remote.StartCalls);
    }

    [Fact]
    public async Task FailedEngineStartReportsAttention()
    {
        var remote = new Remote { AllowStart = false };
        var bridge = new AudioBridgeService(remote);
        Assert.False((await bridge.EnsureReadyAsync()).Success);
        Assert.Equal(AudioBridgeConnectionState.Faulted, bridge.Snapshot.Connection);
        Assert.Single(bridge.Snapshot.Faults);
    }

    [Fact]
    public async Task EngineRestartMovesThroughRecoveringToConnected()
    {
        var remote = new Remote { Connected = true, Running = true };
        var bridge = new AudioBridgeService(remote);
        Assert.True((await bridge.EnsureReadyAsync()).Success);
        remote.Connected = false;
        remote.Running = false;
        bridge.SampleTelemetry(null, null);
        Assert.Equal(AudioBridgeConnectionState.Recovering, bridge.Snapshot.Connection);
        Assert.True((await bridge.EnsureReadyAsync()).Success);
        Assert.Equal(AudioBridgeConnectionState.Connected, bridge.Snapshot.Connection);
    }

    [Fact]
    public async Task BackgroundTelemetryStopsWithoutFurtherRemotePolling()
    {
        var remote = new Remote { Connected = true, Running = true };
        var bridge = new AudioBridgeService(remote);
        bridge.StartTelemetry(null, null);
        for (var attempt = 0; bridge.Snapshot.Telemetry is null && attempt < 20; attempt++)
            await Task.Delay(50);
        Assert.NotNull(bridge.Snapshot.Telemetry);
        remote.Connected = false;
        for (var attempt = 0; bridge.Snapshot.Telemetry!.Status.Connected && attempt < 20; attempt++)
            await Task.Delay(50);
        Assert.False(bridge.Snapshot.Telemetry!.Status.Connected);
        await bridge.StopTelemetryAsync();
        var count = remote.ProbeCalls;
        await Task.Delay(250);
        Assert.Equal(count, remote.ProbeCalls);
    }

    [Fact]
    public async Task WrongEditionIsRejectedWithoutLaunchingAnotherMixer()
    {
        var remote = new Remote { Connected = true, Running = true, Edition = "Potato" };
        var bridge = new AudioBridgeService(remote);
        Assert.False((await bridge.EnsureReadyAsync()).Success);
        Assert.Equal(0, remote.StartCalls);
        Assert.Equal(AudioBridgeConnectionState.UnsupportedEdition, bridge.Snapshot.Connection);
    }

    [Fact]
    public async Task SetterSuccessWithoutReadbackIsNotSuccess()
    {
        var remote = new Remote { Connected = true, Running = true, ApplyWrites = false };
        remote.Values["Strip[4].B1"] = 0;
        var bridge = new AudioBridgeService(remote);
        Assert.False((await bridge.ApplyRouteAsync("setup", 4, "B1", true)).Success);
        Assert.Empty(bridge.Snapshot.OwnedRoutes);
    }

    [Fact]
    public async Task ExternalMutationIsPreservedOnLeaseRelease()
    {
        var remote = new Remote { Connected = true, Running = true };
        remote.Values["Strip[4].B1"] = 0;
        var bridge = new AudioBridgeService(remote);
        var lease = (await bridge.ApplyRouteAsync("setup", 4, "B1", true)).Lease!;
        remote.Values["Strip[4].B1"] = .4f;
        Assert.Equal(AudioLeaseReleaseState.ExternallyChanged, await bridge.ReleaseRouteAsync(lease));
        Assert.Equal(.4f, remote.Values["Strip[4].B1"]);
    }

    [Fact]
    public async Task ConfiguredRouteRepairVerifiesAndCommitsTheOwnedValue()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            var remote = new Remote { Connected = true, Running = true };
            remote.Values["Strip[4].B1"] = 0;
            var bridge = new AudioBridgeService(remote, recoveryPath: path);
            Assert.True((await bridge.RepairConfiguredRouteAsync(4, "B1", true)).Success);
            Assert.Equal(1, remote.Values["Strip[4].B1"]);
            Assert.Empty(bridge.Snapshot.OwnedRoutes);
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task InterruptedRouteIsRestoredFromDurableLease()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            var remote = new Remote { Connected = true, Running = true };
            remote.Values["Strip[4].B1"] = 0;
            var first = new AudioBridgeService(remote, recoveryPath: path);
            Assert.True((await first.ApplyRouteAsync("setup", 4, "B1", true)).Success);
            Assert.True(File.Exists(path));
            Assert.Equal(1, remote.Values["Strip[4].B1"]);
            var restarted = new AudioBridgeService(remote, recoveryPath: path);
            Assert.True((await restarted.RecoverOwnedRoutesAsync()).Success);
            Assert.Equal(0, remote.Values["Strip[4].B1"]);
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task FailedReadbackRetainsRecoveryWhenStateCannotBeRead()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            var remote = new Remote { Connected = true, Running = true };
            remote.Values["Strip[4].B1"] = 0;
            var bridge = new AudioBridgeService(remote, recoveryPath: path);
            Assert.True((await bridge.ApplyRouteAsync("setup", 4, "B1", true)).Success);
            remote.Values.Remove("Strip[4].B1");
            Assert.Equal(AudioLeaseReleaseState.Failed, await bridge.ReleaseRouteAsync(bridge.Snapshot.OwnedRoutes.Single()));
            Assert.True(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RecoveryLeavesExternalRouteAlone()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            var remote = new Remote { Connected = true, Running = true };
            remote.Values["Strip[4].Gain"] = -10;
            var bridge = new AudioBridgeService(remote, recoveryPath: path);
            Assert.True((await bridge.SetFloatAsync("temporary", "Strip[4].Gain", -6)).Success);
            remote.Values["Strip[4].Gain"] = -3;
            var restarted = new AudioBridgeService(remote, recoveryPath: path);
            Assert.True((await restarted.RecoverOwnedRoutesAsync()).Success);
            Assert.Equal(-3, remote.Values["Strip[4].Gain"]);
            Assert.False(File.Exists(path));
            Assert.Contains(restarted.Snapshot.Faults, fault => fault.Fault.Contains("External change"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task InterruptedMicrophoneAssignmentRestoresPriorDevice()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            var remote = new Remote { Connected = true, Running = true };
            remote.Strings["Strip[0].device.name"] = "Owner microphone";
            var first = new AudioBridgeService(remote, recoveryPath: path);
            var applied = await first.SetDeviceAsync("setup", "Strip[0].device.wdm", "Strip[0].device.name",
                "WARDOGS microphone", persistLease: true);
            Assert.True(applied.Success);
            Assert.True(File.Exists(path + ".devices"));
            var restarted = new AudioBridgeService(remote, recoveryPath: path);
            Assert.True((await restarted.RecoverOwnedRoutesAsync()).Success);
            Assert.Equal("Owner microphone", remote.Strings["Strip[0].device.name"]);
            Assert.False(File.Exists(path + ".devices"));
        }
        finally { if (File.Exists(path + ".devices")) File.Delete(path + ".devices"); }
    }

    [Fact]
    public async Task DeviceRecoveryPreservesExternalAssignment()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            var remote = new Remote { Connected = true, Running = true };
            remote.Strings["Strip[0].device.name"] = "Owner microphone";
            var first = new AudioBridgeService(remote, recoveryPath: path);
            Assert.True((await first.SetDeviceAsync("setup", "Strip[0].device.wdm", "Strip[0].device.name",
                "WARDOGS microphone", persistLease: true)).Success);
            remote.Strings["Strip[0].device.name"] = "Another microphone";
            var restarted = new AudioBridgeService(remote, recoveryPath: path);
            Assert.True((await restarted.RecoverOwnedRoutesAsync()).Success);
            Assert.Equal("Another microphone", remote.Strings["Strip[0].device.name"]);
            Assert.False(File.Exists(path + ".devices"));
            Assert.Contains(restarted.Snapshot.Faults, fault => fault.Fault.Contains("External change"));
        }
        finally { if (File.Exists(path + ".devices")) File.Delete(path + ".devices"); }
    }

    [Fact]
    public void DeviceOwnershipRequiresWholeNameOrKnownDriverPrefix()
    {
        Assert.True(AudioBridgeService.MatchesDeviceName("Owner microphone", "Owner microphone"));
        Assert.True(AudioBridgeService.MatchesDeviceName("WDM: Owner microphone", "Owner microphone"));
        Assert.False(AudioBridgeService.MatchesDeviceName("Another Owner microphone", "Owner microphone"));
        Assert.False(AudioBridgeService.MatchesDeviceName("Owner microphone backup", "Owner microphone"));
        Assert.False(AudioBridgeService.MatchesDeviceName("anything", ""));
    }

    [Fact]
    public async Task UnconfirmedDeviceWriteDoesNotClaimOwnership()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            var remote = new Remote { Connected = true, Running = true, ApplyWrites = false };
            remote.Strings["Strip[0].device.name"] = "Owner microphone";
            var bridge = new AudioBridgeService(remote, recoveryPath: path);
            var result = await bridge.SetDeviceAsync("setup", "Strip[0].device.wdm", "Strip[0].device.name",
                "WARDOGS microphone", persistLease: true);
            Assert.False(result.Success);
            Assert.Equal("Owner microphone", remote.Strings["Strip[0].device.name"]);
            Assert.Empty(bridge.OwnedDevices);
            Assert.False(File.Exists(path + ".devices"));
        }
        finally { if (File.Exists(path + ".devices")) File.Delete(path + ".devices"); }
    }

    [Fact]
    public async Task CommittedDeviceAssignmentIsNotRecoveredOnNextLaunch()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            var remote = new Remote { Connected = true, Running = true };
            remote.Strings["Strip[0].device.name"] = "Owner microphone";
            var first = new AudioBridgeService(remote, recoveryPath: path);
            var lease = (await first.SetDeviceAsync("setup", "Strip[0].device.wdm", "Strip[0].device.name",
                "WARDOGS microphone", persistLease: true)).DeviceLease!;
            Assert.True((await first.CommitDeviceAsync(lease)).Success);
            Assert.False(File.Exists(path + ".devices"));
            var restarted = new AudioBridgeService(remote, recoveryPath: path);
            Assert.True((await restarted.RecoverOwnedRoutesAsync()).Success);
            Assert.Equal("WARDOGS microphone", remote.Strings["Strip[0].device.name"]);
        }
        finally { if (File.Exists(path + ".devices")) File.Delete(path + ".devices"); }
    }

    [Fact]
    public async Task RecoveryJournalFailurePreventsRouteAndDeviceWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wardogs-bridge-directory-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var remote = new Remote { Connected = true, Running = true };
            remote.Values["Strip[4].B1"] = 0;
            remote.Strings["Strip[0].device.name"] = "Owner microphone";
            var bridge = new AudioBridgeService(remote, recoveryPath: directory);
            Assert.False((await bridge.ApplyRouteAsync("setup", 4, "B1", true)).Success);
            Assert.Equal(0, remote.Values["Strip[4].B1"]);
            // Block the device-journal path with a directory as well.
            Directory.CreateDirectory(directory + ".devices");
            try
            {
                Assert.False((await bridge.SetDeviceAsync("setup", "Strip[0].device.wdm", "Strip[0].device.name",
                    "WARDOGS microphone", persistLease: true)).Success);
                Assert.Equal("Owner microphone", remote.Strings["Strip[0].device.name"]);
            }
            finally { Directory.Delete(directory + ".devices"); }
        }
        finally
        {
            var pending = directory + ".pending";
            if (File.Exists(pending)) File.Delete(pending);
            var devicePending = directory + ".devices.pending";
            if (File.Exists(devicePending)) File.Delete(devicePending);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task UnresolvedJournalBlocksNewRouteUntilRecovered()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "not-json");
            var remote = new Remote { Connected = true, Running = true };
            remote.Values["Strip[4].B1"] = 0;
            var bridge = new AudioBridgeService(remote, recoveryPath: path);
            Assert.False(bridge.RecoveryReady);
            Assert.False((await bridge.RecoverOwnedRoutesAsync()).Success);
            Assert.False((await bridge.ApplyRouteAsync("new setup", 4, "B1", true)).Success);
            Assert.Equal(0, remote.Values["Strip[4].B1"]);
            Assert.Equal("not-json", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task TamperedRouteJournalCannotWriteUnsupportedParameter()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "[{\"Owner\":\"other\",\"Resource\":\"Bus[0].device.mme\",\"PriorState\":1,\"AppliedState\":0,\"CreatedAt\":\"2026-01-01T00:00:00Z\"}]");
            var remote = new Remote { Connected = true, Running = true };
            remote.Values["Bus[0].device.mme"] = 0;
            var bridge = new AudioBridgeService(remote, recoveryPath: path);
            Assert.False((await bridge.RecoverOwnedRoutesAsync()).Success);
            Assert.Equal(0, remote.Values["Bus[0].device.mme"]);
            Assert.True(File.Exists(path));
            Assert.False(bridge.RecoveryReady);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task NullJournalRemainsBlockedInsteadOfBeingTreatedAsRecovered()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "null");
            var remote = new Remote { Connected = true, Running = true };
            remote.Values["Strip[4].B1"] = 0;
            var bridge = new AudioBridgeService(remote, recoveryPath: path);
            Assert.False((await bridge.RecoverOwnedRoutesAsync()).Success);
            Assert.False(bridge.RecoveryReady);
            Assert.False((await bridge.ApplyRouteAsync("new", 4, "B1", true)).Success);
            Assert.Equal(0, remote.Values["Strip[4].B1"]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task TamperedDeviceJournalCannotWriteUnsupportedRestoreParameter()
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-bridge-test-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path + ".devices", "[{\"Owner\":\"other\",\"Resource\":\"Strip[0].device.wdm\",\"ReadbackResource\":\"Strip[0].device.name\",\"PriorName\":\"Owner microphone\",\"AppliedName\":\"WARDOGS microphone\",\"RestoreParameter\":\"Strip[1].device.wdm\",\"CreatedAt\":\"2026-01-01T00:00:00Z\"}]");
            var remote = new Remote { Connected = true, Running = true };
            remote.Strings["Strip[0].device.name"] = "WARDOGS microphone";
            var bridge = new AudioBridgeService(remote, recoveryPath: path);
            Assert.False((await bridge.RecoverOwnedRoutesAsync()).Success);
            Assert.Equal("WARDOGS microphone", remote.Strings["Strip[0].device.name"]);
            Assert.True(File.Exists(path + ".devices"));
            Assert.False(bridge.RecoveryReady);
        }
        finally { if (File.Exists(path + ".devices")) File.Delete(path + ".devices"); }
    }
}
