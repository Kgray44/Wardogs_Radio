using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class ConfigurationMigrationTests
{
    [Fact]
    public async Task HeadsetAndGameLevelsPersistIndependently()
    {
        var root = Path.Combine(Path.GetTempPath(), "WardogsRadioTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            var config = new AppConfiguration
            {
                MasterVolume = .61,
                GameMasterVolume = .29,
                MicrophoneVolume = 1.6,
                MpvAudioDeviceName = "wasapi/headset",
                GameMpvAudioDeviceName = "wasapi/game"
            };
            config.Profile.Stations[0].Volume = .77;
            config.Profile.Stations[0].GameVolume = .43;
            await store.SaveAsync(config);
            var restored = await store.LoadAsync();
            Assert.Equal(.61, restored.MasterVolume);
            Assert.Equal(.29, restored.GameMasterVolume);
            Assert.Equal(1.6, restored.MicrophoneVolume);
            Assert.Equal("wasapi/headset", restored.MpvAudioDeviceName);
            Assert.Equal("wasapi/game", restored.GameMpvAudioDeviceName);
            Assert.Equal(.77, restored.Profile.Stations[0].Volume);
            Assert.Equal(.43, restored.Profile.Stations[0].GameVolume);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ClipGuardPreferencesMigrateWithoutTouchingUserVolumeLevels()
    {
        var root = Path.Combine(Path.GetTempPath(), "WardogsRadioTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            var config = new AppConfiguration
            {
                SchemaVersion = 10,
                MasterVolume = .61,
                GameMasterVolume = .29,
                ClipGuard = new ClipGuardSettings
                {
                    Mode = ClipGuardMode.Monitor,
                    Preset = ClipGuardPreset.Custom,
                    SafetyCeilingDbfs = -4.5,
                    NearClipThresholdDbfs = -1.2,
                    MaximumReductionDb = 6
                }
            };
            await store.SaveAsync(config);

            var migrated = await store.LoadAsync();

            Assert.Equal(MusicLibraryService.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.Equal(.61, migrated.MasterVolume);
            Assert.Equal(.29, migrated.GameMasterVolume);
            Assert.Equal(ClipGuardMode.Monitor, migrated.ClipGuard.Mode);
            Assert.Equal(ClipGuardPreset.Custom, migrated.ClipGuard.Preset);
            Assert.Equal(-4.5, migrated.ClipGuard.SafetyCeilingDbfs);
            Assert.Equal(-1.2, migrated.ClipGuard.NearClipThresholdDbfs);
            Assert.Equal(6, migrated.ClipGuard.MaximumReductionDb);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExistingClipGuardConfigurationDoesNotLeaseMixerLimiterWithoutAnExplicitChoice()
    {
        var root = Path.Combine(Path.GetTempPath(), "WardogsRadioTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            await store.SaveAsync(new AppConfiguration
            {
                SchemaVersion = 11,
                ClipGuard = new ClipGuardSettings { LimiterEnabled = true }
            });

            var migrated = await store.LoadAsync();

            Assert.Equal(MusicLibraryService.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.False(migrated.ClipGuard.LimiterEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(0, -60)]
    [InlineData(1, 0)]
    [InlineData(1.6, 4.0824)]
    public void MicrophoneSliderMapsToVoicemeeterDb(double level, double expected)
    {
        Assert.InRange(MicrophoneLevel.GainDb(level), expected - .001, expected + .001);
    }

    [Fact]
    public async Task ExistingHeadsetLevelsSeedNewGameLevels()
    {
        var root = Path.Combine(Path.GetTempPath(), "WardogsRadioTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            var old = new AppConfiguration { SchemaVersion = 8, MasterVolume = .37 };
            old.Profile.Stations[0].Volume = .64;
            await store.SaveAsync(old);
            var migrated = await store.LoadAsync();
            Assert.Equal(MusicLibraryService.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.Equal(.37, migrated.GameMasterVolume);
            Assert.Equal(.64, migrated.Profile.Stations[0].GameVolume);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StationPlaylistAndReshuffleChoicePersist()
    {
        var root = Path.Combine(Path.GetTempPath(), "WardogsRadioTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            var config = new AppConfiguration();
            var station = config.Profile.Stations[0];
            station.Source = "C:\\Music\\first.mp3";
            station.PlaylistFiles = ["C:\\Music\\first.mp3", "C:\\Music\\second.flac"];
            station.Shuffle = true;
            station.ShuffleSeed = 107;
            await store.SaveAsync(config);
            var restored = (await store.LoadAsync()).Profile.Stations[0];
            Assert.Equal(station.PlaylistFiles, restored.PlaylistFiles);
            Assert.True(restored.Shuffle);
            Assert.Equal(107, restored.ShuffleSeed);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OlderDefaultControlsBecomeEditableHoldMacroWithoutKeyConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), "WardogsRadioTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            var config = new AppConfiguration { SchemaVersion = 3 };
            var cruise = config.Profile.Stations.Single(x => x.Name == "Cruise");
            cruise.Hotkey = "F9";
            var comms = config.Profile.Macros.Single(x => x.Name == "Comms");
            comms.Activation = MacroActivation.Press;
            comms.Actions = [new RadioAction { Kind = ActionKind.Duck, Value = -18 }];
            comms.ReleaseActions.Clear();
            await store.SaveAsync(config);
            var migrated = await store.LoadAsync();
            Assert.Equal(MusicLibraryService.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.Null(migrated.Profile.Stations.Single(x => x.Name == "Cruise").Hotkey);
            var restored = migrated.Profile.Macros.Single(x => x.Name == "Comms");
            Assert.Equal(MacroActivation.Hold, restored.Activation);
            Assert.Equal(ActionKind.SetMasterGain, restored.Actions.Single().Kind);
            Assert.Equal(ActionKind.RestorePreviousMasterGain, restored.ReleaseActions.Single().Kind);
            Assert.False(restored.ToggleState);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LegacyLoopFlagMigratesToRepeatMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "WardogsRadioTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            var config = new AppConfiguration { SchemaVersion = 6 };
            config.Profile.Stations[0].Loop = false;
            config.Profile.Stations[1].Loop = true;
            await store.SaveAsync(config);
            var stations = (await store.LoadAsync()).Profile.Stations;
            Assert.Equal(StationRepeatMode.Off, stations[0].EffectiveRepeatMode);
            Assert.Equal(StationRepeatMode.Playlist, stations[1].EffectiveRepeatMode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OldSlideshowCompletionDoesNotClaimVerifiedSignalPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "WardogsRadioTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            await store.SaveAsync(new AppConfiguration { SchemaVersion = 7, SetupComplete = true });
            var migrated = await store.LoadAsync();
            Assert.Equal(MusicLibraryService.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.False(migrated.SetupComplete);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LegacyFadeSecondsBecomeConfiguredMilliseconds()
    {
        var root = Path.Combine(Path.GetTempPath(), "WardogsRadioTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            var config = new AppConfiguration { SchemaVersion = 5 };
            var fade = config.Profile.Macros.Single(x => x.Name == "Emergency").Actions[0];
            fade.Kind = ActionKind.FadeToStation;
            fade.Value = .15;
            fade.DelayMilliseconds = 0;
            await store.SaveAsync(config);
            var migrated = await store.LoadAsync();
            var action = migrated.Profile.Macros.Single(x => x.Name == "Emergency").Actions[0];
            Assert.Equal(150, action.DelayMilliseconds);
            Assert.Null(action.Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
