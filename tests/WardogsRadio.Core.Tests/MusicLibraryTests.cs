using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class MusicLibraryTests
{
    [Fact]
    public void OneSongCanBeAssignedToSeveralStationsWithoutDuplicatingTheCue()
    {
        var configuration = new AppConfiguration { Profile = new RadioProfile { Stations = [new Station { Name = "Cruise" }, new Station { Name = "Combat" }] } };
        var source = MusicLibraryService.EnsureSource(configuration.MusicLibrary, "youtube", "https://youtu.be/abcDEF12345", "Vietnam Mix");
        var song = new LibrarySong { SourceId = source.Id, Name = "Fortunate Son", StartSeconds = 763, EndSeconds = 904 };
        configuration.MusicLibrary.Songs.Add(song);
        configuration.Profile.Stations[0].PlaylistEntries.Add(new StationPlaylistEntry { SongId = song.Id });
        configuration.Profile.Stations[1].PlaylistEntries.Add(new StationPlaylistEntry { SongId = song.Id });

        Assert.All(configuration.Profile.Stations, station =>
            Assert.Equal(song.Id, Assert.Single(MusicLibraryService.Resolve(configuration, station)).SongId));
        Assert.Single(configuration.MusicLibrary.Songs);
    }

    [Fact]
    public void DuplicateSongKeepsOneSourceButCreatesASeparateCue()
    {
        var library = new MusicLibrary();
        var source = MusicLibraryService.EnsureSource(library, "mpv", "C:\\Music\\mix.wav", "Mix");
        var original = new LibrarySong { SourceId = source.Id, Name = "Fortunate Son", StartSeconds = 120, EndSeconds = 180 };
        library.Songs.Add(original);

        var duplicate = MusicLibraryService.DuplicateSong(library, original.Id, "Fortunate Son — Combat Edit");

        Assert.NotEqual(original.Id, duplicate.Id);
        Assert.Equal(original.SourceId, duplicate.SourceId);
        Assert.Equal(original.StartSeconds, duplicate.StartSeconds);
        Assert.Equal(original.EndSeconds, duplicate.EndSeconds);
    }

    [Fact]
    public void SplittingSongEditsTheGlobalCueAndAddsOnlyTheCurrentStationEntry()
    {
        var configuration = Fixture();
        var original = Assert.Single(configuration.MusicLibrary.Songs);
        original.StartSeconds = 0;
        original.EndSeconds = 300;
        var cruiseEntry = Assert.Single(configuration.Profile.Stations[0].PlaylistEntries);

        var created = MusicLibraryService.SplitSong(configuration.MusicLibrary, original.Id, 120, 300, "Fortunate Son", true);
        MusicLibraryService.InsertSplitEntry(configuration.Profile.Stations[0], cruiseEntry.Id, created.Id, after: true);

        Assert.Equal(120, original.EndSeconds);
        Assert.Equal(120, created.StartSeconds);
        Assert.Equal(300, created.EndSeconds);
        Assert.Equal([original.Id, created.Id], configuration.Profile.Stations[0].PlaylistEntries.Select(entry => entry.SongId));
        Assert.Equal([original.Id], configuration.Profile.Stations[1].PlaylistEntries.Select(entry => entry.SongId));
    }

    [Fact]
    public void PlaybackProjectionAlwaysReflectsTheCanonicalLibrarySong()
    {
        var configuration = Fixture();
        var song = Assert.Single(configuration.MusicLibrary.Songs);
        song.StartSeconds = 12;
        song.EndSeconds = 42;
        MusicLibraryService.MaterializeStationPlaylist(configuration, configuration.Profile.Stations[0]);
        Assert.Equal(12, Assert.Single(configuration.Profile.Stations[0].PlaylistSongs).StartSeconds);

        MusicLibraryService.UpdateSong(configuration.MusicLibrary, song.Id, "Edited everywhere", 15, 45, 60);
        MusicLibraryService.MaterializeStationPlaylist(configuration, configuration.Profile.Stations[1]);
        var projection = Assert.Single(configuration.Profile.Stations[1].PlaylistSongs);
        Assert.Equal("Edited everywhere", projection.Name);
        Assert.Equal(15, projection.StartSeconds);
        Assert.Equal(45, projection.EndSeconds);
    }

    [Fact]
    public void RemovingAStationEntryDoesNotRemoveTheCanonicalLibrarySong()
    {
        var configuration = Fixture();
        var song = Assert.Single(configuration.MusicLibrary.Songs);
        configuration.Profile.Stations[0].PlaylistEntries.Clear();

        Assert.Contains(configuration.MusicLibrary.Songs, candidate => candidate.Id == song.Id);
        Assert.Empty(MusicLibraryService.Resolve(configuration, configuration.Profile.Stations[0]));
        Assert.Single(MusicLibraryService.Resolve(configuration, configuration.Profile.Stations[1]));
    }

    [Fact]
    public void ReorderingOrShufflingAStationChangesOnlyItsReferences()
    {
        var configuration = Fixture();
        var source = Assert.Single(configuration.MusicLibrary.Sources);
        var second = new LibrarySong { SourceId = source.Id, Name = "Second" };
        configuration.MusicLibrary.Songs.Add(second);
        foreach (var station in configuration.Profile.Stations)
            MusicLibraryService.AddSongToStation(station, second.Id);
        var cruise = configuration.Profile.Stations[0];
        var combat = configuration.Profile.Stations[1];
        var entry = Assert.Single(cruise.PlaylistEntries, item => item.SongId == second.Id);

        MusicLibraryService.MoveStationEntry(cruise, entry.Id, 0);
        MusicLibraryService.ShuffleStationEntries(combat, new Random(7));

        Assert.Equal([second.Id, Assert.Single(configuration.MusicLibrary.Songs, song => song.Name == "Song").Id],
            cruise.PlaylistEntries.Select(item => item.SongId));
        Assert.Equal(2, combat.PlaylistEntries.Count);
        Assert.Equal(2, configuration.MusicLibrary.Songs.Count);
    }

    [Fact]
    public void CreatingLibraryCueValidatesTheRangeWithoutLeavingAnInvalidRecord()
    {
        var library = new MusicLibrary();
        var source = MusicLibraryService.EnsureSource(library, "mpv", "C:\\Music\\mix.wav", "Mix", 120);

        var created = MusicLibraryService.CreateSong(library, source.Id, "Second set", 30, 90, sourceDurationSeconds: 120);
        Assert.Equal(source.Id, created.SourceId);
        Assert.Equal(30, created.StartSeconds);
        Assert.Equal(90, created.EndSeconds);
        Assert.Throws<InvalidOperationException>(() => MusicLibraryService.CreateSong(library, source.Id, "Bad", 110, 121, sourceDurationSeconds: 120));
        Assert.Single(library.Songs);
    }

    [Fact]
    public void DeletingSongCleansEveryStationReference()
    {
        var configuration = Fixture();
        var song = Assert.Single(configuration.MusicLibrary.Songs);

        var affected = MusicLibraryService.DeleteSong(configuration, song.Id);

        Assert.Equal(2, affected.Count);
        Assert.Empty(configuration.MusicLibrary.Songs);
        Assert.All(configuration.Profile.Stations, station => Assert.Empty(station.PlaylistEntries));
    }

    [Fact]
    public void EmptyCanonicalPlaylistClearsTheLegacyPlaybackProjection()
    {
        var configuration = Fixture();
        var station = configuration.Profile.Stations[0];
        MusicLibraryService.MaterializeStationPlaylist(configuration, station);
        Assert.NotEmpty(station.PlaylistSongs);

        station.PlaylistEntries.Clear();
        MusicLibraryService.MaterializeStationPlaylist(configuration, station);

        Assert.Empty(station.PlaylistSongs);
        Assert.Empty(station.PlaylistFiles);
    }

    [Fact]
    public void SourceIdentityDeduplicatesYouTubeUrlsAndLocalPathCasing()
    {
        var library = new MusicLibrary();
        var firstVideo = MusicLibraryService.EnsureSource(library, "youtube", "https://youtu.be/abcDEF12345?t=20", "Mix");
        var secondVideo = MusicLibraryService.EnsureSource(library, "youtube", "https://www.youtube.com/watch?v=abcDEF12345", "Other name");
        var firstFile = MusicLibraryService.EnsureSource(library, "mpv", "C:\\Music\\Track.wav", "Track");
        var secondFile = MusicLibraryService.EnsureSource(library, "mpv", "c:\\music\\track.wav", "Track");

        Assert.Same(firstVideo, secondVideo);
        Assert.Same(firstFile, secondFile);
        Assert.Equal(2, library.Sources.Count);
    }

    [Fact]
    public async Task VersionNineConfigurationMigratesNamedRangesOrderAndSharedSources()
    {
        var root = Path.Combine(Path.GetTempPath(), "wardogs-library-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cruise = new Station { Name = "Cruise", ProviderId = "youtube", Source = "https://www.youtube.com/watch?v=abcDEF12345", PlaylistSongs =
            [
                new() { Source = "abcDEF12345", Name = "Fortunate Son", StartSeconds = 763, EndSeconds = 904 },
                new() { Source = "abcDEF12345", Name = "Paint It, Black", StartSeconds = 1214, EndSeconds = 1431 }
            ] };
            var combat = new Station { Name = "Combat", ProviderId = "youtube", Source = "https://youtu.be/abcDEF12345", PlaylistSongs =
            [
                new() { Source = "abcDEF12345", Name = "Fortunate Son", StartSeconds = 763, EndSeconds = 904 }
            ] };
            var store = new ConfigurationStore(root);
            await store.SaveAsync(new AppConfiguration { SchemaVersion = 9, Profile = new RadioProfile { Stations = [cruise, combat] } });

            var migrated = await store.LoadAsync();
            var cruiseSongs = MusicLibraryService.Resolve(migrated, migrated.Profile.Stations[0]);
            var combatSongs = MusicLibraryService.Resolve(migrated, migrated.Profile.Stations[1]);

            Assert.Equal(MusicLibraryService.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.Single(migrated.MusicLibrary.Sources);
            Assert.Equal(["Fortunate Son", "Paint It, Black"], cruiseSongs.Select(song => song.Name));
            Assert.Equal(763, cruiseSongs[0].StartSeconds);
            Assert.Equal(904, cruiseSongs[0].EndSeconds);
            Assert.Equal(cruiseSongs[0].SongId, Assert.Single(combatSongs).SongId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SingleVideoStationGainsAnInitialWholeSourceSongDuringMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), "wardogs-library-whole-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            await store.SaveAsync(new AppConfiguration { SchemaVersion = 9, Profile = new RadioProfile { Stations =
                [new Station { Name = "Vietnam Radio", ProviderId = "youtube", Source = "https://youtu.be/abcDEF12345" }] } });

            var migrated = await store.LoadAsync();
            var song = Assert.Single(MusicLibraryService.Resolve(migrated, Assert.Single(migrated.Profile.Stations)));

            Assert.Equal("Vietnam Radio", song.Name);
            Assert.Equal(0, song.StartSeconds);
            Assert.Null(song.EndSeconds);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingLocalFileStationGainsOneReusableWholeSourceSongWithoutDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), "wardogs-library-local-whole-source-" + Guid.NewGuid().ToString("N"));
        var media = Path.Combine(root, "mix.wav");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(media, []);
            var store = new ConfigurationStore(root);
            await store.SaveAsync(new AppConfiguration { SchemaVersion = 9, Profile = new RadioProfile { Stations =
                [new Station { Name = "Cruise", ProviderId = "mpv", Source = media }] } });

            var migrated = await store.LoadAsync();
            var station = Assert.Single(migrated.Profile.Stations);
            var song = Assert.Single(MusicLibraryService.Resolve(migrated, station));
            MusicLibraryService.EnsureStationLibrary(migrated, station);

            Assert.Equal("Cruise", song.Name);
            Assert.Equal(0, song.StartSeconds);
            Assert.Null(song.EndSeconds);
            Assert.Single(migrated.MusicLibrary.Sources);
            Assert.Single(migrated.MusicLibrary.Songs);
            Assert.Single(station.PlaylistEntries);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DeletingSourceCleansDependentSongsAndEveryStationReference()
    {
        var configuration = Fixture();
        var source = Assert.Single(configuration.MusicLibrary.Sources);
        var song = Assert.Single(configuration.MusicLibrary.Songs);
        var alternate = MusicLibraryService.DuplicateSong(configuration.MusicLibrary, song.Id);
        MusicLibraryService.AddSongToStation(configuration.Profile.Stations[0], alternate.Id);

        var affected = MusicLibraryService.DeleteSource(configuration, source.Id);

        Assert.Equal(2, affected.Count);
        Assert.Empty(configuration.MusicLibrary.Sources);
        Assert.Empty(configuration.MusicLibrary.Songs);
        Assert.All(configuration.Profile.Stations, station => Assert.Empty(station.PlaylistEntries));
    }

    [Fact]
    public async Task ExternalAndHttpStreamStationsDoNotGainMisleadingSegmentCues()
    {
        var root = Path.Combine(Path.GetTempPath(), "wardogs-library-capabilities-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            await store.SaveAsync(new AppConfiguration { SchemaVersion = 9, Profile = new RadioProfile { Stations =
            [
                new() { Name = "Browser", ProviderId = "external-audio", Source = "session-123" },
                new() { Name = "Stream", ProviderId = "mpv", Source = "https://example.invalid/live" }
            ] } });

            var migrated = await store.LoadAsync();

            Assert.All(migrated.Profile.Stations, station => Assert.Empty(MusicLibraryService.Resolve(migrated, station)));
            Assert.Empty(migrated.MusicLibrary.Songs);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SchemaTenPersistsCanonicalLibraryRecordsWithoutLegacyCueCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "wardogs-library-canonical-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationStore(root);
            var configuration = Fixture();

            await store.SaveAsync(configuration);
            var json = await File.ReadAllTextAsync(store.Path);
            var reloaded = await store.LoadAsync();

            Assert.DoesNotContain("\"PlaylistSongs\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"PlaylistFiles\"", json, StringComparison.Ordinal);
            Assert.Single(MusicLibraryService.Resolve(reloaded, reloaded.Profile.Stations[0]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static AppConfiguration Fixture()
    {
        var configuration = new AppConfiguration { Profile = new RadioProfile { Stations = [new Station { Name = "Cruise" }, new Station { Name = "Combat" }] } };
        var source = MusicLibraryService.EnsureSource(configuration.MusicLibrary, "mpv", "C:\\Music\\mix.wav", "Mix");
        var song = new LibrarySong { SourceId = source.Id, Name = "Song" };
        configuration.MusicLibrary.Songs.Add(song);
        foreach (var station in configuration.Profile.Stations) station.PlaylistEntries.Add(new StationPlaylistEntry { SongId = song.Id });
        return configuration;
    }
}
