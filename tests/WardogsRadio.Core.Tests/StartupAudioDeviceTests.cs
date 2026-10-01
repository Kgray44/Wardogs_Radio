using WardogsRadio.Core;
using WardogsRadio.Playback;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class StartupAudioDeviceTests
{
    static readonly WindowsAudioEndpoint Mic = new(@"SWD\MMDEVAPI\{0.0.1.00000000}.{11111111-1111-1111-1111-111111111111}", "Microphone", true);
    static readonly WindowsAudioEndpoint Headphones = new(@"SWD\MMDEVAPI\{0.0.0.00000000}.{22222222-2222-2222-2222-222222222222}", "Headphones", false);
    static readonly WindowsDefaultAudioSnapshot Defaults = new(Mic, Headphones);

    [Fact]
    public void LastUsedLeavesBothSelectionsUntouched() =>
        Assert.Equal(new StartupAudioDevicePlan(null, null, null, null), StartupAudioDevicePolicy.Plan(
            StartupAudioDeviceMode.LastUsed, "old mic", "old output", Defaults, [Mic, Headphones]));

    [Fact]
    public void WindowsDefaultsUsesInventoryIdentitiesAndDoesNotCompareFriendlyNames()
    {
        var plan = StartupAudioDevicePolicy.Plan(StartupAudioDeviceMode.WindowsDefaults, "old mic", "old output",
            new(Mic with { Name = "renamed microphone" }, Headphones with { Name = "renamed headset" }), [Mic, Headphones]);
        Assert.Equal(Mic, plan.Microphone);
        Assert.Equal(Headphones, plan.Listening);
        var player = new MpvAudioDevice("wasapi/" + Headphones.Id.Split('\\').Last(), "Player-friendly description");
        Assert.Equal(player, ListeningOutputResolver.Resolve(plan.Listening!, [Mic, Headphones], [player]).Device);
    }

    [Fact]
    public void SameStableIdsDoNotRequestAnyMutation()
    {
        var plan = StartupAudioDevicePolicy.Plan(StartupAudioDeviceMode.WindowsDefaults,
            Mic.Id.Split('\\').Last().ToUpperInvariant(), Headphones.Id, Defaults, [Mic, Headphones]);
        Assert.Null(plan.Microphone);
        Assert.Null(plan.Listening);
        Assert.Null(plan.MicrophoneAttention);
        Assert.Null(plan.ListeningAttention);
    }

    [Theory]
    [InlineData("Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)")]
    [InlineData("VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)")]
    [InlineData("WARDOGS virtual microphone")]
    public void VirtualDefaultIsRefusedWithoutGuessingAnotherMicrophone(string name)
    {
        var plan = StartupAudioDevicePolicy.Plan(StartupAudioDeviceMode.WindowsDefaults, "old mic", Headphones.Id,
            Defaults with { Capture = Mic with { Name = name } }, [Mic, Headphones]);
        Assert.Null(plan.Microphone);
        Assert.Contains("VIRTUAL AUDIO ROUTE", plan.MicrophoneAttention);
        Assert.Null(plan.Listening);
    }

    [Fact]
    public void UnavailableDefaultsRetainSavedSelectionsWithTargetedAttention()
    {
        var plan = StartupAudioDevicePolicy.Plan(StartupAudioDeviceMode.WindowsDefaults, "old mic", "old output",
            new(null, Headphones with { IsPresent = false }, "COMException"), [Mic, Headphones]);
        Assert.Null(plan.Microphone);
        Assert.Null(plan.Listening);
        Assert.NotNull(plan.MicrophoneAttention);
        Assert.NotNull(plan.ListeningAttention);
        Assert.Null(StartupAudioDevicePolicy.Plan(StartupAudioDeviceMode.WindowsDefaults, "old mic", "old output",
            Defaults, [Mic with { Id = Mic.Id.Replace("11111111", "33333333") }, Headphones]).Microphone);
    }

    [Fact]
    public void OneDeviceChangePreservesOtherVerification()
    {
        var saved = new SetupVerification { MicrophoneDeviceId = Mic.Id, MonitorDeviceId = Headphones.Id,
            MicrophoneObserved = true, ListeningConfirmed = true, MusicObserved = true, GameReceiveConfirmed = true };
        var microphoneChanged = AudioDeviceVerification.InvalidateMicrophone(saved)!;
        Assert.Null(microphoneChanged.MicrophoneDeviceId);
        Assert.False(microphoneChanged.MicrophoneObserved);
        Assert.True(microphoneChanged.ListeningConfirmed);
        Assert.Equal(Headphones.Id, microphoneChanged.MonitorDeviceId);
        var outputChanged = AudioDeviceVerification.InvalidateListening(saved)!;
        Assert.True(outputChanged.MicrophoneObserved);
        Assert.Equal(Mic.Id, outputChanged.MicrophoneDeviceId);
        Assert.False(outputChanged.ListeningConfirmed);
        Assert.True(outputChanged.MusicObserved);
        Assert.True(outputChanged.GameReceiveConfirmed);
        Assert.Null(StartupAudioDevicePolicy.Plan(StartupAudioDeviceMode.WindowsDefaults,
            Mic.Id, "old output", Defaults, [Mic, Headphones]).Microphone);
    }

    [Fact]
    public async Task OldConfigurationDefaultsToLastUsedAndPreferenceRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "wardogs-startup-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var store = new ConfigurationStore(root);
            await File.WriteAllTextAsync(store.Path, "{\"SchemaVersion\":12,\"MicrophoneDeviceId\":\"saved mic\",\"MonitorDeviceId\":\"saved output\"}");
            var config = await store.LoadAsync();
            Assert.Equal(StartupAudioDeviceMode.LastUsed, config.StartupAudioDeviceMode);
            Assert.Equal("saved mic", config.MicrophoneDeviceId);
            Assert.Equal("saved output", config.MonitorDeviceId);
            config.StartupAudioDeviceMode = StartupAudioDeviceMode.WindowsDefaults;
            await store.SaveAsync(config);
            Assert.Equal(StartupAudioDeviceMode.WindowsDefaults, (await store.LoadAsync()).StartupAudioDeviceMode);
        }
        finally { Directory.Delete(root, true); }
    }
}
