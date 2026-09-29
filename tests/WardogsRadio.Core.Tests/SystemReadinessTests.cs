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
        PlayerOutputResolved = true,
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
        Assert.Equal(OverallReadiness.Ready, readiness.Overall);
        Assert.Equal(ReadinessSeverity.Ready, readiness.Checks.Single(check => check.Id == "microphone").Severity);
        Assert.Equal(ReadinessSeverity.Info, readiness.Checks.Single(check => check.Id == "microphone-observation").Severity);
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
        Assert.Equal(OverallReadiness.Ready, readiness.Overall);
        Assert.Equal(ReadinessSeverity.Ready, readiness.Checks.Single(check => check.Id == "microphone").Severity);
        Assert.Equal(ReadinessSeverity.Ready, readiness.Checks.Single(check => check.Id == "game-output").Severity);
        Assert.Equal(ReadinessSeverity.Info, readiness.Checks.Single(check => check.Id == "game-output-observation").Severity);
    }

    [Fact]
    public void ConfiguredWithoutLiveOrSavedProofStillHasHealthyCore()
    {
        var config = Config();
        config.SetupVerification = null;
        var readiness = SystemReadinessService.Evaluate(config, Evidence());
        Assert.Equal(OverallReadiness.Ready, readiness.Overall);
        Assert.Equal(readiness.CoreTotal, readiness.CorePassed);
        Assert.Equal(0, readiness.LiveVerificationPassed);
        Assert.Equal(ReadinessSeverity.Info, readiness.Checks.Single(check => check.Id == "game-confirmation").Severity);
    }

    [Fact]
    public void QuietConfiguredMicrophoneDoesNotBlockSystemReady()
    {
        var config = Config();
        config.SetupVerification = null;
        var readiness = SystemReadinessService.Evaluate(config, Evidence());
        Assert.Equal(OverallReadiness.Ready, readiness.Overall);
        Assert.Equal(ReadinessSeverity.Ready, readiness.Checks.Single(check => check.Id == "microphone").Severity);
        Assert.Equal(ReadinessSeverity.Info, readiness.Checks.Single(check => check.Id == "microphone-observation").Severity);
        Assert.False(readiness.Checks.Single(check => check.Id == "microphone-observation").BlocksCoreReadiness);
    }

    [Fact]
    public void LiveMicObservationAddsEvidenceButIsNotRequiredForHealth()
    {
        var config = Config();
        config.SetupVerification = null;
        var quiet = SystemReadinessService.Evaluate(config, Evidence());
        var observed = SystemReadinessService.Evaluate(config, Evidence() with { MicrophoneObserved = true });
        Assert.Equal(OverallReadiness.Ready, quiet.Overall);
        Assert.Equal(OverallReadiness.Ready, observed.Overall);
        Assert.Equal(quiet.LiveVerificationPassed + 1, observed.LiveVerificationPassed);
        Assert.Equal(ReadinessSeverity.Ready, observed.Checks.Single(check => check.Id == "microphone-observation").Severity);
    }

    [Fact]
    public void UnpluggedSelectedDevicesHaveIndependentWarningsAndBlockReady()
    {
        var bothMissing = SystemReadinessService.Evaluate(Config(), Evidence() with
        {
            MicrophonePresent = false, HeadphonesPresent = false
        });
        Assert.Equal(OverallReadiness.NeedsAttention, bothMissing.Overall);
        Assert.False(bothMissing.MicrophonePresent);
        Assert.False(bothMissing.ListeningOutputPresent);
        Assert.Equal(ReadinessSeverity.NeedsAction, bothMissing.Checks.Single(check => check.Id == "microphone").Severity);
        Assert.Equal(ReadinessSeverity.NeedsAction, bothMissing.Checks.Single(check => check.Id == "listening").Severity);

        var microphoneReturned = SystemReadinessService.Evaluate(Config(), Evidence() with { HeadphonesPresent = false });
        Assert.True(microphoneReturned.MicrophonePresent);
        Assert.False(microphoneReturned.ListeningOutputPresent);
        Assert.Equal(OverallReadiness.NeedsAttention, microphoneReturned.Overall);

        var bothReturned = SystemReadinessService.Evaluate(Config(), Evidence());
        Assert.True(bothReturned.MicrophonePresent);
        Assert.True(bothReturned.ListeningOutputPresent);
        Assert.Equal(OverallReadiness.Ready, bothReturned.Overall);
    }

    [Fact]
    public void PresentHeadsetWithUnresolvedPlayerNeedsRepairRatherThanDisconnection()
    {
        var readiness = SystemReadinessService.Evaluate(Config(), Evidence() with { PlayerOutputResolved = false });
        Assert.True(readiness.ListeningOutputPresent);
        Assert.False(readiness.ListeningPlayerResolved);
        Assert.Equal(ReadinessSeverity.Ready, readiness.Checks.Single(check => check.Id == "listening").Severity);
        Assert.Equal(ReadinessSeverity.NeedsAction, readiness.Checks.Single(check => check.Id == "listening-player").Severity);
        Assert.Equal(OverallReadiness.NeedsAttention, readiness.Overall);
    }

    [Fact]
    public void PlaybackTestIsSeparateFromConfiguredAndMappedState()
    {
        var quiet = SystemReadinessService.Evaluate(Config(), Evidence());
        var tested = SystemReadinessService.Evaluate(Config(), Evidence() with { PlaybackPathVerified = true });
        Assert.Equal(ReadinessSeverity.Info, quiet.Checks.Single(check => check.Id == "listening-playback-path").Severity);
        Assert.True(tested.ListeningPlaybackVerified);
        Assert.Equal(ReadinessSeverity.Ready, tested.Checks.Single(check => check.Id == "listening-playback-path").Severity);
    }

    [Fact]
    public void FailedActivePlayerSwitchDoesNotCallPresentHeadsetDisconnected()
    {
        var readiness = SystemReadinessService.Evaluate(Config(), Evidence() with { PlayerOutputSwitchFailed = true });
        Assert.True(readiness.ListeningOutputPresent);
        Assert.True(readiness.ListeningPlayerResolved);
        Assert.True(readiness.ListeningPlayerConnectionFailed);
        Assert.Equal(ReadinessSeverity.Ready, readiness.Checks.Single(check => check.Id == "listening").Severity);
        Assert.Equal(ReadinessSeverity.NeedsAction, readiness.Checks.Single(check => check.Id == "listening-player").Severity);
    }
}
