using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class SystemReadinessTests
{
    static AppConfiguration Config() => new()
    {
        MicrophoneDeviceId = "mic-a", MonitorDeviceId = "headphones-a", MicrophoneStripIndex = 0,
        MusicStripIndex = 4, GameBus = "B1",
        SetupVerification = new SetupVerification
        {
            VerifiedAt = DateTimeOffset.UtcNow.AddDays(-1), MicrophoneDeviceId = "mic-a",
            MonitorDeviceId = "headphones-a", MicrophoneStripIndex = 0, MusicStripIndex = 4,
            GameBus = "B1", GameOutputDeviceId = "b1-a", VoicemeeterEdition = "Banana",
            MicrophoneObserved = true, ListeningConfirmed = true, MusicObserved = true,
            MixerBusObserved = true, WindowsBusObserved = true, GameReceiveConfirmed = true
        }
    };

    static ReadinessEvidence Evidence() => new()
    {
        MpvAvailable = true, VoicemeeterInstalled = true, VoicemeeterConnected = true,
        VoicemeeterEdition = "Banana", MicrophonePresent = true, HeadphonesPresent = true,
        MicrophoneAssigned = true, MicrophoneRoutedToGame = true, MusicRoutedToGame = true,
        MusicPlayerTargetsGame = true, GameEndpointPresent = true, GameEndpointId = "b1-a"
    };

    [Fact]
    public void VerifiedTopologyRemainsReadyAfterRestartWhileQuiet()
    {
        var readiness = SystemReadinessService.Evaluate(Config(), Evidence());
        Assert.Equal(OverallReadiness.Ready, readiness.Overall);
        Assert.Equal(readiness.CoreTotal, readiness.CorePassed);
    }

    [Fact]
    public void OptionalProvidersAndControllersCannotDowngradeCore()
    {
        var readiness = SystemReadinessService.Evaluate(Config(), Evidence());
        Assert.Equal(OverallReadiness.Ready, readiness.Overall);
        Assert.Equal(ReadinessSeverity.Optional, readiness.Checks.Single(check => check.Id == "soundcloud").Severity);
        Assert.Equal(ReadinessSeverity.Optional, readiness.Checks.Single(check => check.Id == "apple-music").Severity);
        Assert.Equal(ReadinessSeverity.Optional, readiness.Checks.Single(check => check.Id == "controllers").Severity);
    }

    [Fact]
    public void MicrophoneChangeOnlyInvalidatesMicrophoneVerification()
    {
        var config = Config();
        config.MicrophoneDeviceId = "mic-b";
        var readiness = SystemReadinessService.Evaluate(config, Evidence());
        Assert.Equal(OverallReadiness.NeedsVerification, readiness.Overall);
        Assert.Equal(ReadinessSeverity.NotTested, readiness.Checks.Single(check => check.Id == "microphone").Severity);
        Assert.Equal(ReadinessSeverity.Ready, readiness.Checks.Single(check => check.Id == "listening").Severity);
    }

    [Fact]
    public void ConflictingIndependentB1TelemetryCannotBeReady()
    {
        var readiness = SystemReadinessService.Evaluate(Config(), Evidence() with { TelemetryConflict = true });
        Assert.Equal(OverallReadiness.NeedsAttention, readiness.Overall);
        Assert.Equal(ReadinessSeverity.Warning, readiness.Checks.Single(check => check.Id == "game-output").Severity);
    }

    [Fact]
    public void UnsupportedRunningEditionCannotBeReady()
    {
        var readiness = SystemReadinessService.Evaluate(Config(), Evidence() with { VoicemeeterEdition = "Potato" });
        Assert.Equal(OverallReadiness.NeedsAttention, readiness.Overall);
        Assert.Equal(ReadinessSeverity.Error, readiness.Checks.Single(check => check.Id == "voicemeeter").Severity);
    }

    [Fact]
    public void UnsupportedGameBusCannotBeReady()
    {
        var config = Config();
        config.GameBus = "B2";
        var readiness = SystemReadinessService.Evaluate(config, Evidence());
        Assert.Equal(OverallReadiness.NeedsAttention, readiness.Overall);
        Assert.Equal(ReadinessSeverity.Error, readiness.Checks.Single(check => check.Id == "supported-topology").Severity);
    }

    [Fact]
    public void ChangedWindowsB1EndpointInvalidatesOnlyGameVerification()
    {
        var readiness = SystemReadinessService.Evaluate(Config(), Evidence() with { GameEndpointId = "b1-b" });
        Assert.Equal(OverallReadiness.NeedsVerification, readiness.Overall);
        Assert.Equal(ReadinessSeverity.Ready, readiness.Checks.Single(check => check.Id == "microphone").Severity);
        Assert.Equal(ReadinessSeverity.NotTested, readiness.Checks.Single(check => check.Id == "game-output").Severity);
    }

    [Fact]
    public void ConfiguredWithoutLiveOrSavedProofNeedsVerification()
    {
        var config = Config();
        config.SetupVerification = null;
        var readiness = SystemReadinessService.Evaluate(config, Evidence());
        Assert.Equal(OverallReadiness.NeedsVerification, readiness.Overall);
        Assert.Equal(ReadinessSeverity.NotTested, readiness.Checks.Single(check => check.Id == "game-confirmation").Severity);
    }
}
