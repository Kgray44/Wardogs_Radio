using System.IO.Compression;
using System.Text.Json;
using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class BackupTransferTests
{
    [Fact]
    public async Task StationExportIncludesCanonicalSongAndSourceDependenciesExactlyOnce()
    {
        await using var fixture = new PackageFixture();
        var configuration = fixture.Configuration();
        var combat = configuration.Profile.Stations.Single(station => station.Name == "Combat");
        var path = fixture.Path("combat.wradio");
        var service = fixture.Service;

        var summary = await service.ExportAsync(configuration, new WrRadioExportSelection
        {
            Contents = WrRadioContent.Stations,
            StationIds = [combat.Id],
            PackageName = "Combat Pack"
        }, path);
        var package = await service.OpenAsync(path);

        Assert.Equal(1, summary.Stations);
        Assert.Single(package.Stations);
        Assert.Single(package.Songs);
        Assert.Single(package.Sources);
        Assert.Equal(package.Sources[0].Id, package.Songs[0].SourceId);
        Assert.Equal(package.Songs[0].Id, Assert.Single(package.Stations[0].PlaylistEntries).SongId);
    }

    [Fact]
    public async Task FullBackupRoundTripsIntoAnEmptyConfiguration()
    {
        await using var fixture = new PackageFixture();
        var original = fixture.Configuration();
        original.CrossfadeSeconds = 1.2;
        original.MonitorDeviceId = "device-guid";
        var path = fixture.Path("full.wradio");
        await fixture.Service.ExportAsync(original, WrRadioExportSelection.FullBackup(), path);
        var package = await fixture.Service.OpenAsync(path);

        var plan = fixture.Service.BuildImportPlan(new AppConfiguration { Profile = new RadioProfile(), MusicLibrary = new MusicLibrary() }, package,
            new WrRadioImportOptions { Contents = WrRadioContent.All, LibraryConflictResolution = LibraryConflictResolution.ReplaceExisting, StationConflictResolution = StationConflictResolution.ReplaceExisting });

        Assert.Equal(original.Profile.Stations.Count, plan.ProposedConfiguration.Profile.Stations.Count);
        Assert.Equal(original.MusicLibrary.Songs.Count, plan.ProposedConfiguration.MusicLibrary.Songs.Count);
        Assert.Equal(original.MusicLibrary.Sources.Count, plan.ProposedConfiguration.MusicLibrary.Sources.Count);
        Assert.Equal(original.CrossfadeSeconds, plan.ProposedConfiguration.CrossfadeSeconds);
        Assert.Equal("device-guid", plan.ProposedConfiguration.MonitorDeviceId);
    }

    [Fact]
    public async Task FullBackupIgnoresPartialSelectionAndAlwaysCarriesTheWholeConfiguration()
    {
        await using var fixture = new PackageFixture();
        var configuration = fixture.Configuration();
        var onlyStation = Assert.Single(configuration.Profile.Stations, station => station.Name == "Cruise");

        var package = fixture.Service.BuildPackage(configuration, new WrRadioExportSelection
        {
            PackageType = WrRadioPackageType.FullBackup,
            Contents = WrRadioContent.Stations,
            StationIds = [onlyStation.Id]
        });

        Assert.Equal(WrRadioContent.All, package.Manifest.Contents);
        Assert.Equal(configuration.Profile.Stations.Count, package.Stations.Count);
        Assert.Equal(configuration.Profile.Macros.Count, package.Macros.Count);
    }

    [Fact]
    public async Task MissingAudioHardwareIsRetainedButCalledOutInTheImportPlan()
    {
        await using var fixture = new PackageFixture();
        var configuration = fixture.Configuration();
        configuration.MonitorDeviceId = "headset-not-present";
        Assert.Single(configuration.Profile.Macros).ControllerBindings = ["controller:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hotas-not-present")) + ":button-1"];
        var package = fixture.Service.BuildPackage(configuration, WrRadioExportSelection.FullBackup());

        var plan = fixture.Service.BuildImportPlan(new AppConfiguration { Profile = new RadioProfile(), MusicLibrary = new MusicLibrary() }, package,
            new WrRadioImportOptions
            {
                Contents = WrRadioContent.All,
                AvailableAudioDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "different-device" },
                AvailableControllerDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "different-controller" }
            });

        Assert.Equal("headset-not-present", plan.ProposedConfiguration.MonitorDeviceId);
        Assert.Contains(plan.Warnings, warning => warning.Contains("not currently available", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, warning => warning.Contains("controller", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConflictingSongIsCopiedAndStationReferenceIsRemapped()
    {
        await using var fixture = new PackageFixture();
        var incoming = fixture.Configuration();
        var song = Assert.Single(incoming.MusicLibrary.Songs);
        song.StartSeconds = 20;
        var path = fixture.Path("conflict.wradio");
        await fixture.Service.ExportAsync(incoming, WrRadioExportSelection.FullBackup(), path);
        var package = await fixture.Service.OpenAsync(path);
        var current = fixture.Configuration();
        var currentSource = Assert.Single(current.MusicLibrary.Sources);
        var currentSong = Assert.Single(current.MusicLibrary.Songs);
        currentSource.Id = Assert.Single(incoming.MusicLibrary.Sources).Id;
        currentSong.Id = song.Id;
        currentSong.SourceId = currentSource.Id;
        currentSong.StartSeconds = 10;

        var plan = fixture.Service.BuildImportPlan(current, package, new WrRadioImportOptions
        {
            Contents = WrRadioContent.All,
            LibraryConflictResolution = LibraryConflictResolution.ImportAsCopy,
            StationConflictResolution = StationConflictResolution.KeepBoth
        });

        Assert.Contains(plan.Conflicts, conflict => conflict.Category == "Library song");
        Assert.Equal(2, plan.ProposedConfiguration.MusicLibrary.Songs.Count);
        var importedSongId = plan.SongIds[song.Id];
        Assert.NotEqual(song.Id, importedSongId);
        var importedStation = plan.ProposedConfiguration.Profile.Stations.Single(station => station.Name.StartsWith("Cruise (Imported)", StringComparison.Ordinal));
        Assert.Equal(importedSongId, Assert.Single(importedStation.PlaylistEntries).SongId);
    }

    [Fact]
    public async Task UnsafeArchivePathIsRejectedBeforeConfigurationCanBePlanned()
    {
        await using var fixture = new PackageFixture();
        var path = fixture.Path("unsafe.wradio");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../config.json");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("{}");
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.OpenAsync(path));
        Assert.Contains("unsafe archive path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingRequiredContentFileIsRejectedWithAnActionableError()
    {
        await using var fixture = new PackageFixture();
        var path = fixture.Path("missing-content.wradio");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            await using var stream = manifest.Open();
            await JsonSerializer.SerializeAsync(stream, new WrRadioPackageManifest { Contents = WrRadioContent.Stations }, WrRadioPackageService.JsonOptions);
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.OpenAsync(path));
        Assert.Contains("configuration/stations.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokenSongToSourceReferenceIsRejectedBeforeAnyImportPlanExists()
    {
        await using var fixture = new PackageFixture();
        var path = fixture.Path("broken-reference.wradio");
        await fixture.Service.ExportAsync(fixture.Configuration(), WrRadioExportSelection.FullBackup(), path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry("library/songs.json")!.Delete();
            var entry = archive.CreateEntry("library/songs.json");
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, new[] { new LibrarySong { SourceId = Guid.NewGuid(), Name = "Broken cue" } }, WrRadioPackageService.JsonOptions);
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.OpenAsync(path));
        Assert.Contains("missing media source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SamePlayableSourceWithDifferentMetadataReusesTheCanonicalSourceByDefault()
    {
        await using var fixture = new PackageFixture();
        var current = fixture.Configuration();
        var incoming = fixture.Configuration();
        var currentSource = Assert.Single(current.MusicLibrary.Sources);
        var importedSource = Assert.Single(incoming.MusicLibrary.Sources);
        importedSource.Source = currentSource.Source;
        importedSource.Name = "Renamed source";

        var package = fixture.Service.BuildPackage(incoming, WrRadioExportSelection.FullBackup());
        var plan = fixture.Service.BuildImportPlan(current, package, new WrRadioImportOptions { Contents = WrRadioContent.All });

        Assert.Equal(currentSource.Id, plan.SourceIds[importedSource.Id]);
        Assert.Single(plan.ProposedConfiguration.MusicLibrary.Sources);
        Assert.Contains(plan.Conflicts, conflict => conflict.Category == "Media source");
    }

    [Fact]
    public async Task ExportExcludesBindingsWhenTheirCategoriesAreNotSelected()
    {
        await using var fixture = new PackageFixture();
        var configuration = fixture.Configuration();
        var station = Assert.Single(configuration.Profile.Stations, station => station.Name == "Cruise");
        station.Hotkey = "F9";
        station.ControllerBinding = "controller:one:button-1";

        var package = fixture.Service.BuildPackage(configuration, new WrRadioExportSelection
        {
            Contents = WrRadioContent.Stations,
            StationIds = [station.Id]
        });

        Assert.Null(Assert.Single(package.Stations).Hotkey);
        Assert.Null(Assert.Single(package.Stations).ControllerBinding);
        Assert.Empty(package.Macros);
    }

    [Fact]
    public async Task ImportCreatesSafetyBackupBeforeSavingTheProposedConfiguration()
    {
        await using var fixture = new PackageFixture();
        var exported = fixture.Configuration();
        var packagePath = fixture.Path("source.wradio");
        await fixture.Service.ExportAsync(exported, WrRadioExportSelection.FullBackup(), packagePath);
        var store = new ConfigurationStore(fixture.Root);
        var current = new AppConfiguration { Profile = new RadioProfile(), MusicLibrary = new MusicLibrary() };
        await store.SaveAsync(current);

        var plan = await fixture.Service.ImportAsync(store, current, packagePath);
        var reloaded = await store.LoadAsync();
        var backupPath = Assert.Single(Directory.EnumerateFiles(fixture.Service.BackupsDirectory, "Before-Import-*.wradio"));
        var backup = await fixture.Service.OpenAsync(backupPath);

        Assert.Equal(WrRadioPackageType.FullBackup, backup.Manifest.PackageType);
        Assert.Equal(plan.ProposedConfiguration.Profile.Stations.Count, reloaded.Profile.Stations.Count);
    }

    [Fact]
    public async Task SafetyBackupsUseUniqueNamesWhenCreatedWithinTheSameSecond()
    {
        await using var fixture = new PackageFixture();
        var configuration = fixture.Configuration();

        var first = await fixture.Service.CreateSafetyBackupAsync(configuration, "Import");
        var second = await fixture.Service.CreateSafetyBackupAsync(configuration, "Import");

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task RestoreReplacesTheActiveConfigurationOnlyAfterCreatingASafetyBackup()
    {
        await using var fixture = new PackageFixture();
        var backupConfiguration = fixture.Configuration();
        var packagePath = fixture.Path("restore.wradio");
        await fixture.Service.ExportAsync(backupConfiguration, WrRadioExportSelection.FullBackup(), packagePath);
        var store = new ConfigurationStore(fixture.Path("active"));
        var current = new AppConfiguration { Profile = new RadioProfile { Name = "Before restore" }, MusicLibrary = new MusicLibrary() };
        await store.SaveAsync(current);

        var plan = await fixture.Service.RestoreAsync(store, current, packagePath);
        var reloaded = await store.LoadAsync();

        Assert.Equal(plan.ProposedConfiguration.Profile.Stations.Count, reloaded.Profile.Stations.Count);
        Assert.NotEmpty(Directory.EnumerateFiles(fixture.Service.BackupsDirectory, "Before-Restore-*.wradio"));
    }

    [Fact]
    public async Task FailedCommitLeavesTheActiveConfigurationUntouched()
    {
        await using var fixture = new PackageFixture();
        var packagePath = fixture.Path("atomic.wradio");
        await fixture.Service.ExportAsync(fixture.Configuration(), WrRadioExportSelection.FullBackup(), packagePath);
        var store = new ConfigurationStore(fixture.Path("atomic-target"));
        var current = new AppConfiguration { Profile = new RadioProfile { Name = "Original" }, MusicLibrary = new MusicLibrary() };
        await store.SaveAsync(current);
        var originalJson = await File.ReadAllTextAsync(store.Path);
        Directory.CreateDirectory(store.Path + ".new");

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.ImportAsync(store, current, packagePath));

        Assert.Equal(originalJson, await File.ReadAllTextAsync(store.Path));
    }

    [Fact]
    public async Task EmbeddedLocalMediaIsPackagedAndImportedToManagedLocation()
    {
        await using var fixture = new PackageFixture();
        var media = fixture.Path("source.mp3");
        await File.WriteAllBytesAsync(media, [1, 2, 3, 4]);
        var configuration = fixture.Configuration(media);
        var packagePath = fixture.Path("media.wradio");
        var summary = await fixture.Service.ExportAsync(configuration, new WrRadioExportSelection { Contents = WrRadioContent.All, IncludeLocalMedia = true }, packagePath);
        var store = new ConfigurationStore(fixture.Path("target"));
        var targetService = new WrRadioPackageService(fixture.Path("target"));
        var current = new AppConfiguration { Profile = new RadioProfile(), MusicLibrary = new MusicLibrary() };
        await store.SaveAsync(current);

        var plan = await targetService.ImportAsync(store, current, packagePath);
        var importedSource = Assert.Single(plan.ProposedConfiguration.MusicLibrary.Sources);

        Assert.Equal(1, summary.MediaFiles);
        Assert.True(File.Exists(importedSource.Source));
        Assert.Equal(await File.ReadAllBytesAsync(media), await File.ReadAllBytesAsync(importedSource.Source));
    }

    [Fact]
    public async Task MigrationPipelineUpgradesOlderPackageSchema()
    {
        await using var fixture = new PackageFixture();
        var source = fixture.Configuration();
        var path = fixture.Path("old.wradio");
        await fixture.Service.ExportAsync(source, WrRadioExportSelection.FullBackup(), path);
        await DowngradeManifestAsync(path);
        var migrated = new WrRadioPackageService(fixture.Root, [new TestMigration()]);

        var package = await migrated.OpenAsync(path);

        Assert.Equal(WrRadioPackageService.CurrentFormatVersion, package.Manifest.FormatVersion);
    }

    static async Task DowngradeManifestAsync(string packagePath)
    {
        string json;
        using (var archive = ZipFile.OpenRead(packagePath))
        using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open())) json = await reader.ReadToEndAsync();
        var manifest = JsonSerializer.Deserialize<WrRadioPackageManifest>(json, WrRadioPackageService.JsonOptions)!;
        manifest.FormatVersion = 0;
        var temp = packagePath + ".rewrite";
        using (var source = ZipFile.OpenRead(packagePath))
        using (var destination = ZipFile.Open(temp, ZipArchiveMode.Create))
            foreach (var entry in source.Entries)
            {
                var copy = destination.CreateEntry(entry.FullName);
                await using var output = copy.Open();
                if (entry.FullName == "manifest.json")
                    await JsonSerializer.SerializeAsync(output, manifest, WrRadioPackageService.JsonOptions);
                else
                {
                    await using var input = entry.Open();
                    await input.CopyToAsync(output);
                }
            }
        File.Move(temp, packagePath, true);
    }

    sealed class TestMigration : IWrRadioPackageMigration
    {
        public int FromVersion => 0;
        public int ToVersion => 1;
        public WrRadioPackage Migrate(WrRadioPackage package) => package;
    }

    sealed class PackageFixture : IAsyncDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wardogs-package-" + Guid.NewGuid().ToString("N"));
        public WrRadioPackageService Service { get; }
        public PackageFixture() { Directory.CreateDirectory(Root); Service = new WrRadioPackageService(Root); }
        public string Path(string file) => System.IO.Path.Combine(Root, file);
        public AppConfiguration Configuration(string? mediaPath = null)
        {
            var source = new MediaSource { ProviderId = "mpv", Source = mediaPath ?? @"C:\Music\mix.mp3", Name = "Mix" };
            var song = new LibrarySong { SourceId = source.Id, Name = "Fortunate Son", StartSeconds = 5, EndSeconds = 15 };
            var cruise = new Station { Name = "Cruise", PlaylistEntries = [new StationPlaylistEntry { SongId = song.Id }] };
            var combat = new Station { Name = "Combat", PlaylistEntries = [new StationPlaylistEntry { SongId = song.Id }] };
            var macro = new RadioMacro { Name = "Combat shortcut", Actions = [new RadioAction { Kind = ActionKind.ActivateStation, StationId = combat.Id }] };
            return new AppConfiguration { Profile = new RadioProfile { Stations = [cruise, combat], Macros = [macro] }, MusicLibrary = new MusicLibrary { Sources = [source], Songs = [song] } };
        }
        public ValueTask DisposeAsync() { if (Directory.Exists(Root)) Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
}
