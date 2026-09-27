using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class OutputHealthTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1d, 0d)]
    [InlineData(.707945784d, -3d)]
    [InlineData(.501187234d, -6d)]
    public void LinearPeaksConvertToDbfs(double linear, double expectedDbfs)
    {
        var level = AudioLevelSnapshot.FromLinear(true, linear);

        Assert.Equal(expectedDbfs, level.PeakDbfs!.Value, 2);
        Assert.Equal(-expectedDbfs, level.DigitalHeadroomDb!.Value, 2);
    }

    [Fact]
    public void SilenceAndUnavailableMetersAreNotConflated()
    {
        var silence = AudioLevelSnapshot.FromLinear(true, 0);
        var unavailable = AudioLevelSnapshot.Unavailable;

        Assert.Equal(double.NegativeInfinity, silence.PeakDbfs);
        Assert.Null(unavailable.PeakDbfs);
        Assert.Null(unavailable.DigitalHeadroomDb);
    }

    [Fact]
    public void ClassifiesSafeHealthyHotNearClipAndClip()
    {
        var settings = new ClipGuardSettings { Mode = ClipGuardMode.Monitor };
        var controller = new ClipGuardController();

        Assert.Equal(OutputHealthState.Safe, Sample(controller, settings, .1).State);
        Assert.Equal(OutputHealthState.Healthy, Sample(controller, settings, .7, 1).State);
        Assert.Equal(OutputHealthState.Hot, Sample(controller, settings, .8, 2).State);
        Assert.Equal(OutputHealthState.NearClip, Sample(controller, settings, .95, 3).State);
        Assert.Equal(OutputHealthState.Clip, Sample(controller, settings, 1, 4).State);
    }

    [Fact]
    public void PeakHoldsAreIndependentAndExpireOnTheirOwnSchedule()
    {
        var controller = new ClipGuardController();
        var settings = new ClipGuardSettings { Mode = ClipGuardMode.Monitor, PeakHoldMilliseconds = 1000 };
        var first = controller.Sample(settings, Level(.8), Level(.1), Level(.7), false, Start);
        var held = controller.Sample(settings, Level(.1), Level(.9), Level(.1), false, Start.AddMilliseconds(500));
        var expired = controller.Sample(settings, Level(.1), Level(.1), Level(.1), false, Start.AddMilliseconds(1100));

        Assert.Equal(first.Music.PeakDbfs, held.MusicPeakHoldDbfs);
        Assert.Equal(first.GameBus.PeakDbfs, held.GamePeakHoldDbfs);
        Assert.Equal(held.Microphone.PeakDbfs, held.MicrophonePeakHoldDbfs);
        Assert.Equal(expired.GameBus.PeakDbfs, expired.GamePeakHoldDbfs);
        Assert.Equal(expired.Music.PeakDbfs, expired.MusicPeakHoldDbfs);
    }

    [Fact]
    public void ClipLatchPersistsThenExpiresAndCanBeCleared()
    {
        var controller = new ClipGuardController();
        var settings = new ClipGuardSettings { Mode = ClipGuardMode.Monitor, ClipLatchMilliseconds = 1000 };
        var clipped = Sample(controller, settings, 1, 0);
        var latched = Sample(controller, settings, .1, 500);
        var expired = Sample(controller, settings, .1, 1001);

        Assert.True(clipped.ClipLatched);
        Assert.True(latched.ClipLatched);
        Assert.False(expired.ClipLatched);
        controller.ClearClipLatch();
        Assert.False(Sample(controller, settings, .1, 1100).ClipLatched);
    }

    [Fact]
    public void LastClipCapturesTheObservedCombinedMixAssessment()
    {
        var controller = new ClipGuardController();
        var settings = new ClipGuardSettings { Mode = ClipGuardMode.Monitor };
        var health = controller.Sample(settings, Level(.6), Level(.6), Level(1), false, Start);

        Assert.NotNull(health.LastClipAt);
        Assert.Equal(0, health.LastClipGameDbfs!.Value, 3);
        Assert.Equal("Combined mix overload: music and microphone are individually below the hot range, but B1 is near full scale.",
            health.LastClipDiagnosis);
    }

    [Fact]
    public void ProtectionRequiresPersistentOverloadAndNeverChangesSettings()
    {
        var controller = new ClipGuardController();
        var settings = new ClipGuardSettings { AttackMilliseconds = 50, MaximumReductionDb = 2 };
        var originalCeiling = settings.SafetyCeilingDbfs;
        var transient = Sample(controller, settings, 1, 0, canProtect: true);
        var protectedMix = Sample(controller, settings, 1, 150, canProtect: true);

        Assert.Equal(0, transient.ProtectionReductionDb, 3);
        Assert.InRange(protectedMix.ProtectionReductionDb, .01, settings.MaximumReductionDb);
        Assert.Equal(originalCeiling, settings.SafetyCeilingDbfs);
        Assert.Equal(ClipGuardMode.Protect, settings.Mode);
    }

    [Fact]
    public void MonitorOrUnavailableTelemetryReturnsProtectionToNeutral()
    {
        var controller = new ClipGuardController();
        var settings = new ClipGuardSettings { AttackMilliseconds = 50 };
        Sample(controller, settings, 1, 0, canProtect: true);
        var protectedMix = Sample(controller, settings, 1, 150, canProtect: true);
        var unavailable = controller.Sample(settings, Level(.8), Level(.2), AudioLevelSnapshot.Unavailable, true,
            Start.AddMilliseconds(200));
        settings.Mode = ClipGuardMode.Monitor;
        var monitoring = Sample(controller, settings, 1, 300, canProtect: true);

        Assert.True(protectedMix.ProtectionReductionDb > 0);
        Assert.Equal(0, unavailable.ProtectionReductionDb, 3);
        Assert.Equal(OutputHealthState.Unavailable, unavailable.State);
        Assert.Equal(0, monitoring.ProtectionReductionDb, 3);
    }

    [Fact]
    public void RecoveryWaitsThenReleasesAtConfiguredRate()
    {
        var controller = new ClipGuardController();
        var settings = new ClipGuardSettings { AttackMilliseconds = 50, RecoveryDelayMilliseconds = 1000, RecoveryDbPerSecond = 1 };
        Sample(controller, settings, 1, 0, canProtect: true);
        var protectedMix = Sample(controller, settings, 1, 200, canProtect: true);
        var waiting = Sample(controller, settings, .1, 700, canProtect: true);
        var recovering = Sample(controller, settings, .1, 1800, canProtect: true);

        Assert.True(protectedMix.ProtectionReductionDb > 0);
        Assert.Equal(protectedMix.ProtectionReductionDb, waiting.ProtectionReductionDb, 3);
        Assert.True(recovering.ProtectionReductionDb < waiting.ProtectionReductionDb);
    }

    [Fact]
    public void BroadcastLevelTestUsesObservedB1PeakForAnExplicitRecommendation()
    {
        var settings = new ClipGuardSettings { SafetyCeilingDbfs = -3 };
        var test = new BroadcastLevelTestSession();
        test.Start(Start);
        var health = new ClipGuardController().Sample(settings, Level(.7), Level(.5), Level(.95), false, Start);
        test.Observe(health);
        var result = test.Stop(settings, Start.AddSeconds(10));

        Assert.Equal(health.Music.PeakDbfs, result.MusicPeakDbfs);
        Assert.Equal(health.Microphone.PeakDbfs, result.MicrophonePeakDbfs);
        Assert.Equal(health.GameBus.PeakDbfs, result.GamePeakDbfs);
        Assert.Equal(OutputHealthState.NearClip, result.State);
        Assert.True(result.HasRecommendation);
        Assert.InRange(result.RecommendedGameMusicReductionDb, 3, 4);
    }

    [Fact]
    public void BroadcastLevelTestDoesNotRecommendFromMissingTelemetry()
    {
        var settings = new ClipGuardSettings();
        var test = new BroadcastLevelTestSession();
        test.Start(Start);
        var unavailable = new ClipGuardController().Sample(settings, Level(.7), Level(.5), AudioLevelSnapshot.Unavailable, false, Start);
        test.Observe(unavailable);
        var result = test.Stop(settings, Start.AddSeconds(1));

        Assert.Equal(OutputHealthState.Unavailable, result.State);
        Assert.False(result.HasRecommendation);
    }

    [Fact]
    public void ProtectionGainComposesAfterRequestedGameGainWithoutChangingIt()
    {
        var requestedMaster = 1d;
        var stationGain = .8d;
        var effective = ClipGuardMath.EffectiveGameGain(requestedMaster, stationGain, Math.Pow(10, -4d / 20d));

        Assert.Equal(1d, requestedMaster);
        Assert.Equal(.8d, stationGain);
        Assert.InRange(effective, .504, .506);
    }

    [Fact]
    public void OffAndMonitorModesContinueMeteringButNeverReduceGain()
    {
        foreach (var mode in new[] { ClipGuardMode.Off, ClipGuardMode.Monitor })
        {
            var controller = new ClipGuardController();
            var settings = new ClipGuardSettings { Mode = mode, AttackMilliseconds = 10 };
            Sample(controller, settings, 1, 0, canProtect: true);
            var observed = Sample(controller, settings, 1, 300, canProtect: true);

            Assert.Equal(OutputHealthState.Clip, observed.State);
            Assert.Equal(0, observed.ProtectionReductionDb, 3);
            Assert.Equal(1, observed.ProtectionGain, 3);
        }
    }

    [Fact]
    public void MicrophoneOnlyClipProducesWarningNotMusicOvercorrection()
    {
        var controller = new ClipGuardController();
        var settings = new ClipGuardSettings { Mode = ClipGuardMode.Protect, AttackMilliseconds = 50 };
        OutputHealthSnapshot? health = null;
        for (var index = 0; index < 15; index++)
            health = controller.Sample(settings, Level(.05), Level(1), Level(1), true,
                Start.AddMilliseconds(index * 40));

        Assert.NotNull(health);
        Assert.Equal(0, health.ProtectionReductionDb);
        Assert.Equal(1, health.ProtectionGain);
        Assert.Contains("Microphone is too hot", health.Diagnosis);
    }

    [Fact]
    public void ProtectAttenuatesCombinedGameFeedAtFortyMillisecondSamples()
    {
        var controller = new ClipGuardController();
        var settings = new ClipGuardSettings { Mode = ClipGuardMode.Protect, AttackMilliseconds = 50 };
        OutputHealthSnapshot? health = null;
        for (var index = 0; index < 15; index++)
            health = controller.Sample(settings, Level(.6), Level(.6), Level(1), true,
                Start.AddMilliseconds(index * 40));

        Assert.NotNull(health);
        Assert.True(health.AutoProtectionAvailable);
        Assert.True(health.ProtectionReductionDb > 0);
        Assert.True(ClipGuardMath.EffectiveGameGain(1, 1, health.ProtectionGain) < 1);
    }

    static AudioLevelSnapshot Level(double linear) => AudioLevelSnapshot.FromLinear(true, linear);

    static OutputHealthSnapshot Sample(ClipGuardController controller, ClipGuardSettings settings, double gamePeak,
        int offsetMilliseconds = 0, bool canProtect = false) =>
        controller.Sample(settings, Level(.2), Level(.2), Level(gamePeak), canProtect,
            Start.AddMilliseconds(offsetMilliseconds));
}
