using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WardogsRadio.Core;

/// <summary>Content groups understood by the portable WARDOGS Radio package format.</summary>
[Flags]
public enum WrRadioContent
{
    None = 0,
    Stations = 1 << 0,
    LibrarySongs = 1 << 1,
    MediaSources = 1 << 2,
    Macros = 1 << 3,
    KeyboardBindings = 1 << 4,
    ControllerBindings = 1 << 5,
    PlaybackSettings = 1 << 6,
    AudioRoutingSettings = 1 << 7,
    GeneralSettings = 1 << 8,
    ListeningHistory = 1 << 9,
    All = Stations | LibrarySongs | MediaSources | Macros | KeyboardBindings | ControllerBindings |
          PlaybackSettings | AudioRoutingSettings | GeneralSettings | ListeningHistory
}

public enum WrRadioPackageType { Selection, FullBackup }
public enum LibraryConflictResolution { ImportAsCopy, KeepExisting, ReplaceExisting }
public enum StationConflictResolution { KeepBoth, KeepExisting, ReplaceExisting, Skip }

public sealed class WrRadioExportSelection
{
    public WrRadioContent Contents { get; set; } = WrRadioContent.All;
    public List<Guid> StationIds { get; set; } = [];
    public List<Guid> SongIds { get; set; } = [];
    public List<Guid> MacroIds { get; set; } = [];
    public bool IncludeLocalMedia { get; set; }
    public string? PackageName { get; set; }
    public string? Description { get; set; }
    public WrRadioPackageType PackageType { get; set; } = WrRadioPackageType.Selection;

    public static WrRadioExportSelection FullBackup() => new()
    {
        Contents = WrRadioContent.All,
        PackageType = WrRadioPackageType.FullBackup
    };
}

public sealed class WrRadioImportOptions
{
    public WrRadioContent Contents { get; set; } = WrRadioContent.All;
    public List<Guid> StationIds { get; set; } = [];
    public List<Guid> SongIds { get; set; } = [];
    public List<Guid> MacroIds { get; set; } = [];
    public LibraryConflictResolution LibraryConflictResolution { get; set; } = LibraryConflictResolution.ImportAsCopy;
    public StationConflictResolution StationConflictResolution { get; set; } = StationConflictResolution.KeepBoth;
    /// <summary>Optional live endpoint IDs supplied by the host UI for safe hardware warnings.</summary>
    public ISet<string> AvailableAudioDeviceIds { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Optional live controller IDs supplied by the host UI for safe hardware warnings.</summary>
    public ISet<string> AvailableControllerDeviceIds { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class WrRadioPackageManifest
{
    public const string FormatIdentifier = "wardogs-radio-package";
    public string Format { get; set; } = FormatIdentifier;
    public int FormatVersion { get; set; } = WrRadioPackageService.CurrentFormatVersion;
    public string ApplicationVersion { get; set; } = "development";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid PackageId { get; set; } = Guid.NewGuid();
    public string PackageName { get; set; } = "WARDOGS Radio Export";
    public string? Description { get; set; }
    public WrRadioPackageType PackageType { get; set; }
    public WrRadioContent Contents { get; set; }
    public List<WrRadioMediaAsset> MediaAssets { get; set; } = [];
}

public sealed class WrRadioMediaAsset
{
    public Guid SourceId { get; set; }
    public string ArchivePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Length { get; set; }
}

public sealed class WrRadioSettings
{
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = "WARDOGS";
    public bool SetupComplete { get; set; }
    public bool ListeningHistoryEnabled { get; set; } = true;
    public ClipGuardSettings ClipGuard { get; set; } = new();
    public PlaybackMode DefaultPlaybackMode { get; set; }
    public bool CrossfadeEnabled { get; set; }
    public double CrossfadeSeconds { get; set; }
    public TransitionCurve Curve { get; set; }
    public double MasterVolume { get; set; }
    public double GameMasterVolume { get; set; }
    public double MicrophoneVolume { get; set; }
    public bool MicrophoneVolumeInitialized { get; set; }
    public Dictionary<string, YouTubeTrackMetadata> YouTubeDurationCache { get; set; } = [];
    public Dictionary<string, double> LocalDurationCache { get; set; } = [];
    public string? MpvPath { get; set; }
    public string? MpvAudioDeviceName { get; set; }
    public string? GameMpvAudioDeviceName { get; set; }
    public string? YtDlpPath { get; set; }
    public string? MonitorDeviceId { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public string GameBus { get; set; } = "B1";
    public int? MusicStripIndex { get; set; }
    public int? MicrophoneStripIndex { get; set; }
    public int? AutoMusicRouteStrip { get; set; }
    public bool? AutoMusicPreviousA1 { get; set; }
    public bool? AutoMusicPreviousB1 { get; set; }
    public string? AutoMusicPreviousHeadsetDeviceName { get; set; }
    public string? AutoMusicPreviousGameDeviceName { get; set; }
    public int? AutoMusicPreviousStripIndex { get; set; }
    public int? AutoMicrophoneStrip { get; set; }
    public string? AutoMicrophonePreviousDeviceName { get; set; }
    public bool? AutoMicrophonePreviousA1 { get; set; }
    public bool? AutoMicrophonePreviousB1 { get; set; }
    public int? AutoMicrophonePreviousStripIndex { get; set; }

    public static WrRadioSettings From(AppConfiguration source) => new()
    {
        ProfileId = source.Profile.Id, ProfileName = source.Profile.Name,
        SetupComplete = source.SetupComplete, ListeningHistoryEnabled = source.ListeningHistoryEnabled,
        ClipGuard = Clone(source.ClipGuard), DefaultPlaybackMode = source.DefaultPlaybackMode,
        CrossfadeEnabled = source.CrossfadeEnabled, CrossfadeSeconds = source.CrossfadeSeconds, Curve = source.Curve,
        MasterVolume = source.MasterVolume, GameMasterVolume = source.GameMasterVolume,
        MicrophoneVolume = source.MicrophoneVolume, MicrophoneVolumeInitialized = source.MicrophoneVolumeInitialized,
        YouTubeDurationCache = Clone(source.YouTubeDurationCache), LocalDurationCache = Clone(source.LocalDurationCache),
        MpvPath = source.MpvPath, MpvAudioDeviceName = source.MpvAudioDeviceName, GameMpvAudioDeviceName = source.GameMpvAudioDeviceName,
        YtDlpPath = source.YtDlpPath, MonitorDeviceId = source.MonitorDeviceId, MicrophoneDeviceId = source.MicrophoneDeviceId,
        GameBus = source.GameBus, MusicStripIndex = source.MusicStripIndex, MicrophoneStripIndex = source.MicrophoneStripIndex,
        AutoMusicRouteStrip = source.AutoMusicRouteStrip, AutoMusicPreviousA1 = source.AutoMusicPreviousA1, AutoMusicPreviousB1 = source.AutoMusicPreviousB1,
        AutoMusicPreviousHeadsetDeviceName = source.AutoMusicPreviousHeadsetDeviceName, AutoMusicPreviousGameDeviceName = source.AutoMusicPreviousGameDeviceName,
        AutoMusicPreviousStripIndex = source.AutoMusicPreviousStripIndex, AutoMicrophoneStrip = source.AutoMicrophoneStrip,
        AutoMicrophonePreviousDeviceName = source.AutoMicrophonePreviousDeviceName, AutoMicrophonePreviousA1 = source.AutoMicrophonePreviousA1,
        AutoMicrophonePreviousB1 = source.AutoMicrophonePreviousB1, AutoMicrophonePreviousStripIndex = source.AutoMicrophonePreviousStripIndex
    };

    public void Apply(AppConfiguration target, WrRadioContent contents)
    {
        if (contents.HasFlag(WrRadioContent.GeneralSettings))
        {
            target.Profile.Id = ProfileId == Guid.Empty ? target.Profile.Id : ProfileId;
            target.Profile.Name = string.IsNullOrWhiteSpace(ProfileName) ? target.Profile.Name : ProfileName;
            target.SetupComplete = SetupComplete;
            target.ListeningHistoryEnabled = ListeningHistoryEnabled;
            target.YouTubeDurationCache = Clone(YouTubeDurationCache);
            target.LocalDurationCache = Clone(LocalDurationCache);
            target.MpvPath = MpvPath;
            target.YtDlpPath = YtDlpPath;
        }
        if (contents.HasFlag(WrRadioContent.PlaybackSettings))
        {
            target.DefaultPlaybackMode = DefaultPlaybackMode; target.CrossfadeEnabled = CrossfadeEnabled;
            target.CrossfadeSeconds = CrossfadeSeconds; target.Curve = Curve; target.MasterVolume = MasterVolume;
            target.GameMasterVolume = GameMasterVolume; target.ClipGuard = Clone(ClipGuard);
        }
        if (contents.HasFlag(WrRadioContent.AudioRoutingSettings))
        {
            target.MicrophoneVolume = MicrophoneVolume; target.MicrophoneVolumeInitialized = MicrophoneVolumeInitialized;
            target.MpvAudioDeviceName = MpvAudioDeviceName; target.GameMpvAudioDeviceName = GameMpvAudioDeviceName;
            target.MonitorDeviceId = MonitorDeviceId; target.MicrophoneDeviceId = MicrophoneDeviceId; target.GameBus = GameBus;
            target.MusicStripIndex = MusicStripIndex; target.MicrophoneStripIndex = MicrophoneStripIndex;
            target.AutoMusicRouteStrip = AutoMusicRouteStrip; target.AutoMusicPreviousA1 = AutoMusicPreviousA1;
            target.AutoMusicPreviousB1 = AutoMusicPreviousB1; target.AutoMusicPreviousHeadsetDeviceName = AutoMusicPreviousHeadsetDeviceName;
            target.AutoMusicPreviousGameDeviceName = AutoMusicPreviousGameDeviceName; target.AutoMusicPreviousStripIndex = AutoMusicPreviousStripIndex;
            target.AutoMicrophoneStrip = AutoMicrophoneStrip; target.AutoMicrophonePreviousDeviceName = AutoMicrophonePreviousDeviceName;
            target.AutoMicrophonePreviousA1 = AutoMicrophonePreviousA1; target.AutoMicrophonePreviousB1 = AutoMicrophonePreviousB1;
            target.AutoMicrophonePreviousStripIndex = AutoMicrophonePreviousStripIndex;
        }
    }

    static T Clone<T>(T source) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source, WrRadioPackageService.JsonOptions), WrRadioPackageService.JsonOptions)!;
}

public sealed class WrRadioPackage
{
    public WrRadioPackageManifest Manifest { get; set; } = new();
    public List<Station> Stations { get; set; } = [];
    public List<MediaSource> Sources { get; set; } = [];
    public List<LibrarySong> Songs { get; set; } = [];
    public List<RadioMacro> Macros { get; set; } = [];
    public List<ListeningHistoryEntry> ListeningHistory { get; set; } = [];
    public WrRadioSettings? Settings { get; set; }
    public string SourcePath { get; set; } = "";
}

public sealed record WrRadioPackageSummary(string PackageName, WrRadioPackageType PackageType, int Stations, int Songs,
    int Sources, int Macros, int MediaFiles, long EmbeddedMediaBytes, WrRadioContent Contents);
public sealed record WrRadioConflict(string Category, Guid Id, string Name, string Message);
public sealed class WrRadioImportPlan
{
    public required AppConfiguration ProposedConfiguration { get; init; }
    public required WrRadioPackage Package { get; init; }
    public required WrRadioPackageSummary Summary { get; init; }
    public List<WrRadioConflict> Conflicts { get; } = [];
    public List<string> Warnings { get; } = [];
    public Dictionary<Guid, Guid> SourceIds { get; } = [];
    public Dictionary<Guid, Guid> SongIds { get; } = [];
    public Dictionary<Guid, Guid> StationIds { get; } = [];
    public Dictionary<Guid, Guid> MacroIds { get; } = [];
    public List<ListeningHistoryEntry> HistoryEntries { get; } = [];
}

public interface IWrRadioPackageMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    WrRadioPackage Migrate(WrRadioPackage package);
}

/// <summary>ZIP package reader/writer, dependency resolver, validation and safe import planner.</summary>
public sealed class WrRadioPackageService
{
    public const int CurrentFormatVersion = 1;
    const int MaximumEntries = 10_000;
    const int MaximumObjectsPerCategory = 100_000;
    const long MaximumArchiveEntryBytes = 512L * 1024 * 1024;
    const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    readonly string _root;
    readonly IReadOnlyList<IWrRadioPackageMigration> _migrations;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WrRadioPackageService(string root, IEnumerable<IWrRadioPackageMigration>? migrations = null)
    {
        _root = root;
        _migrations = (migrations ?? []).OrderBy(migration => migration.FromVersion).ToList();
    }

    public string BackupsDirectory => Path.Combine(_root, "Backups");
    public string ImportedMediaDirectory => Path.Combine(_root, "ImportedMedia");

    public WrRadioPackage BuildPackage(AppConfiguration configuration, WrRadioExportSelection selection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(selection);
        var isFullBackup = selection.PackageType == WrRadioPackageType.FullBackup;
        var contents = isFullBackup ? WrRadioContent.All : selection.Contents;
        IReadOnlyCollection<Guid> stationSelection = isFullBackup ? Array.Empty<Guid>() : selection.StationIds;
        IReadOnlyCollection<Guid> songSelection = isFullBackup ? Array.Empty<Guid>() : selection.SongIds;
        IReadOnlyCollection<Guid> macroSelection = isFullBackup ? Array.Empty<Guid>() : selection.MacroIds;
        var stations = Pick(configuration.Profile.Stations, stationSelection, contents.HasFlag(WrRadioContent.Stations));
        var macros = Pick(configuration.Profile.Macros, macroSelection,
            contents.HasFlag(WrRadioContent.Macros) || contents.HasFlag(WrRadioContent.KeyboardBindings) || contents.HasFlag(WrRadioContent.ControllerBindings));

        // Macros with station actions must travel with the station and its canonical dependencies.
        var selectedStationIds = stations.Select(station => station.Id).ToHashSet();
        foreach (var action in macros.SelectMany(macro => AllActions(macro)).Where(action => action.StationId is not null))
        {
            var station = configuration.Profile.Stations.FirstOrDefault(candidate => candidate.Id == action.StationId);
            if (station is not null && selectedStationIds.Add(station.Id)) stations.Add(station);
        }
        var selectedSongIds = new HashSet<Guid>(songSelection);
        if (contents.HasFlag(WrRadioContent.LibrarySongs) && songSelection.Count == 0)
            selectedSongIds.UnionWith(configuration.MusicLibrary.Songs.Select(song => song.Id));
        foreach (var station in stations) selectedSongIds.UnionWith(station.PlaylistEntries.Select(entry => entry.SongId));
        var songs = configuration.MusicLibrary.Songs.Where(song => selectedSongIds.Contains(song.Id)).ToList();
        var sourceIds = songs.Select(song => song.SourceId).ToHashSet();
        if (contents.HasFlag(WrRadioContent.MediaSources) && songSelection.Count == 0 && stationSelection.Count == 0)
            sourceIds.UnionWith(configuration.MusicLibrary.Sources.Select(source => source.Id));
        var sources = configuration.MusicLibrary.Sources.Where(source => sourceIds.Contains(source.Id)).ToList();
        var manifest = new WrRadioPackageManifest
        {
            ApplicationVersion = AppVersion(), PackageName = SafePackageName(selection.PackageName, selection.PackageType),
            Description = selection.Description?.Trim(), PackageType = selection.PackageType, Contents = contents
        };
        var packagedStations = Clone(stations);
        var packagedMacros = Clone(macros);
        if (!contents.HasFlag(WrRadioContent.KeyboardBindings))
        {
            foreach (var station in packagedStations) station.Hotkey = null;
            foreach (var macro in packagedMacros) { macro.Hotkey = null; macro.KeyboardBindings = []; }
        }
        if (!contents.HasFlag(WrRadioContent.ControllerBindings))
        {
            foreach (var station in packagedStations) station.ControllerBinding = null;
            foreach (var macro in packagedMacros) macro.ControllerBindings = [];
        }
        return new WrRadioPackage
        {
            Manifest = manifest,
            Stations = packagedStations, Sources = Clone(sources), Songs = Clone(songs), Macros = packagedMacros,
            Settings = contents.HasFlag(WrRadioContent.GeneralSettings) || contents.HasFlag(WrRadioContent.PlaybackSettings) || contents.HasFlag(WrRadioContent.AudioRoutingSettings)
                ? WrRadioSettings.From(configuration) : null
        };
    }

    public async Task<WrRadioPackageSummary> ExportAsync(AppConfiguration configuration, WrRadioExportSelection selection,
        string destinationPath, CancellationToken cancellationToken = default)
    {
        Log("Package.Export", $"Preparing {selection.PackageType} package.");
        var package = BuildPackage(configuration, selection);
        if (package.Manifest.Contents.HasFlag(WrRadioContent.ListeningHistory))
            package.ListeningHistory = (await new ListeningHistoryStore(_root).ReadAsync(cancellationToken)).ToList();
        destinationPath = EnsureExtension(destinationPath);
        var temporaryPath = destinationPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        try
        {
            if (selection.IncludeLocalMedia) PrepareLocalMediaManifest(package);
            await using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteJsonAsync(archive, "manifest.json", package.Manifest, cancellationToken);
                await WriteJsonAsync(archive, "configuration/stations.json", package.Stations, cancellationToken);
                await WriteJsonAsync(archive, "configuration/macros.json", package.Macros, cancellationToken);
                if (package.Settings is not null) await WriteJsonAsync(archive, "configuration/settings.json", package.Settings, cancellationToken);
                await WriteJsonAsync(archive, "library/sources.json", package.Sources, cancellationToken);
                await WriteJsonAsync(archive, "library/songs.json", package.Songs, cancellationToken);
                if (package.Manifest.Contents.HasFlag(WrRadioContent.ListeningHistory))
                    await WriteJsonAsync(archive, "listening/history.json", package.ListeningHistory, cancellationToken);
                await WriteJsonAsync(archive, "metadata/export-info.json", new { package.Manifest.PackageId, package.Manifest.CreatedUtc, ExportedSecrets = "None" }, cancellationToken);
                if (selection.IncludeLocalMedia) await AddLocalMediaAsync(archive, package, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, true);
            Log("Package.Export", $"Wrote package '{destinationPath}'.");
            return Summarize(package);
        }
        catch (Exception error)
        {
            TryDelete(temporaryPath);
            Log("Package.Export", $"Failed: {error.GetType().Name}: {error.Message}");
            throw;
        }
    }

    public async Task<WrRadioPackage> OpenAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        Log("Package.Open", $"Opening '{packagePath}'.");
        if (!File.Exists(packagePath)) throw new FileNotFoundException("The WARDOGS Radio package was not found.", packagePath);
        if (new FileInfo(packagePath).Length > MaximumArchiveBytes) throw new InvalidDataException("The package is larger than the supported safety limit.");
        using var archive = ZipFile.OpenRead(packagePath);
        ValidateArchive(archive);
        var manifest = await ReadJsonAsync<WrRadioPackageManifest>(archive, "manifest.json", required: true, cancellationToken)
            ?? throw new InvalidDataException("The package manifest is missing or invalid.");
        ValidateManifestAndRequiredFiles(manifest, archive);
        _ = await ReadJsonAsync<JsonElement>(archive, "metadata/export-info.json", required: true, cancellationToken);
        var package = new WrRadioPackage
        {
            Manifest = manifest,
            Stations = await ReadJsonAsync<List<Station>>(archive, "configuration/stations.json", false, cancellationToken) ?? [],
            Macros = await ReadJsonAsync<List<RadioMacro>>(archive, "configuration/macros.json", false, cancellationToken) ?? [],
            Settings = await ReadJsonAsync<WrRadioSettings>(archive, "configuration/settings.json", false, cancellationToken),
            Sources = await ReadJsonAsync<List<MediaSource>>(archive, "library/sources.json", false, cancellationToken) ?? [],
            Songs = await ReadJsonAsync<List<LibrarySong>>(archive, "library/songs.json", false, cancellationToken) ?? [],
            ListeningHistory = await ReadJsonAsync<List<ListeningHistoryEntry>>(archive, "listening/history.json", false, cancellationToken) ?? [],
            SourcePath = packagePath
        };
        package = Migrate(package);
        ValidatePackage(package, archive);
        Log("Package.Validate", $"Validated package {package.Manifest.PackageId} ({package.Manifest.FormatVersion}).");
        return package;
    }

    public WrRadioImportPlan BuildImportPlan(AppConfiguration current, WrRadioPackage package, WrRadioImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(package);
        options ??= new WrRadioImportOptions { Contents = package.Manifest.Contents };
        var proposed = Clone(current);
        var plan = new WrRadioImportPlan { ProposedConfiguration = proposed, Package = package, Summary = Summarize(package) };
        var selectedStations = Pick(package.Stations, options.StationIds, options.Contents.HasFlag(WrRadioContent.Stations));
        var selectedSongs = Pick(package.Songs, options.SongIds, options.Contents.HasFlag(WrRadioContent.LibrarySongs));
        var selectedMacros = Pick(package.Macros, options.MacroIds, options.Contents.HasFlag(WrRadioContent.Macros) ||
            options.Contents.HasFlag(WrRadioContent.KeyboardBindings) || options.Contents.HasFlag(WrRadioContent.ControllerBindings));
        foreach (var station in selectedStations)
            foreach (var entry in station.PlaylistEntries)
                if (selectedSongs.All(song => song.Id != entry.SongId)) selectedSongs.Add(package.Songs.Single(song => song.Id == entry.SongId));
        foreach (var macro in selectedMacros)
            foreach (var action in AllActions(macro).Where(action => action.StationId is not null))
                if (selectedStations.All(station => station.Id != action.StationId)) selectedStations.Add(package.Stations.Single(station => station.Id == action.StationId));
        foreach (var station in selectedStations)
            foreach (var entry in station.PlaylistEntries)
                if (selectedSongs.All(song => song.Id != entry.SongId)) selectedSongs.Add(package.Songs.Single(song => song.Id == entry.SongId));
        var selectedSourceIds = selectedSongs.Select(song => song.SourceId).ToHashSet();
        var selectedSources = package.Sources.Where(source => selectedSourceIds.Contains(source.Id) ||
            options.Contents.HasFlag(WrRadioContent.MediaSources)).ToList();

        foreach (var incoming in selectedSources)
            plan.SourceIds[incoming.Id] = ImportSource(proposed, incoming, options.LibraryConflictResolution, plan);
        foreach (var incoming in selectedSongs)
            plan.SongIds[incoming.Id] = ImportSong(proposed, incoming, plan.SourceIds[incoming.SourceId], options.LibraryConflictResolution, plan);
        foreach (var incoming in selectedStations)
            plan.StationIds[incoming.Id] = ImportStation(proposed, incoming, plan.SongIds, options.StationConflictResolution, plan,
                options.Contents.HasFlag(WrRadioContent.KeyboardBindings), options.Contents.HasFlag(WrRadioContent.ControllerBindings));
        foreach (var incoming in selectedMacros)
            plan.MacroIds[incoming.Id] = ImportMacro(proposed, incoming, plan.StationIds, options.LibraryConflictResolution, plan,
                options.Contents.HasFlag(WrRadioContent.KeyboardBindings), options.Contents.HasFlag(WrRadioContent.ControllerBindings));
        if (options.Contents.HasFlag(WrRadioContent.ListeningHistory))
            plan.HistoryEntries.AddRange(package.ListeningHistory.Select(entry => entry with
            {
                StationId = entry.StationId is { } stationId && plan.StationIds.TryGetValue(stationId, out var mappedStation) ? mappedStation : entry.StationId,
                SongId = entry.SongId is { } songId && plan.SongIds.TryGetValue(songId, out var mappedSong) ? mappedSong : entry.SongId,
                SourceId = entry.SourceId is { } sourceId && plan.SourceIds.TryGetValue(sourceId, out var mappedSource) ? mappedSource : entry.SourceId
            }));
        package.Settings?.Apply(proposed, options.Contents);
        AddHardwareWarnings(package.Settings, options, plan.Warnings);
        AddControllerWarnings(selectedStations, selectedMacros, options, plan.Warnings);
        MusicLibraryService.NormalizeAndMigrate(proposed);
        Log("Package.BuildImportPlan", $"Built plan for {selectedStations.Count} station(s), {selectedSongs.Count} song(s), and {selectedMacros.Count} macro(s).");
        return plan;
    }

    public async Task<string> CreateSafetyBackupAsync(AppConfiguration configuration, string reason, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BackupsDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd-HHmmss");
        var path = NextAvailablePath(Path.Combine(BackupsDirectory, $"Before-{SanitizeFileName(reason)}-{timestamp}.wradio"));
        var selection = WrRadioExportSelection.FullBackup();
        selection.PackageName = $"Before {reason}";
        await ExportAsync(configuration, selection, path, cancellationToken);
        var oldBackups = Directory.EnumerateFiles(BackupsDirectory, "Before-*.wradio")
            .OrderByDescending(File.GetLastWriteTimeUtc).Skip(10).ToList();
        foreach (var oldBackup in oldBackups) TryDelete(oldBackup);
        Log("Package.Backup", $"Created safety backup '{path}'.");
        return path;
    }

    public async Task<WrRadioImportPlan> ImportAsync(ConfigurationStore store, AppConfiguration current, string packagePath,
        WrRadioImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        var package = await OpenAsync(packagePath, cancellationToken);
        var plan = BuildImportPlan(current, package, options);
        await CreateSafetyBackupAsync(current, "Import", cancellationToken);
        await CommitAsync(store, plan, cancellationToken);
        if (options?.Contents.HasFlag(WrRadioContent.ListeningHistory) != false && package.Manifest.Contents.HasFlag(WrRadioContent.ListeningHistory))
            await new ListeningHistoryStore(_root).ImportAsync(plan.HistoryEntries, replace: false, cancellationToken);
        return plan;
    }

    public async Task<WrRadioImportPlan> RestoreAsync(ConfigurationStore store, AppConfiguration current, string packagePath,
        CancellationToken cancellationToken = default)
    {
        var package = await OpenAsync(packagePath, cancellationToken);
        var requiredContents = WrRadioContent.All & ~WrRadioContent.ListeningHistory;
        if (package.Manifest.PackageType != WrRadioPackageType.FullBackup ||
            (package.Manifest.Contents & requiredContents) != requiredContents)
            throw new InvalidDataException("Only a complete WARDOGS Radio backup can replace the active configuration.");
        var blank = new AppConfiguration { Profile = new RadioProfile(), MusicLibrary = new MusicLibrary() };
        var plan = BuildImportPlan(blank, package, new WrRadioImportOptions
        {
            Contents = WrRadioContent.All, LibraryConflictResolution = LibraryConflictResolution.ReplaceExisting,
            StationConflictResolution = StationConflictResolution.ReplaceExisting
        });
        await CreateSafetyBackupAsync(current, "Restore", cancellationToken);
        await CommitAsync(store, plan, cancellationToken);
        if (package.Manifest.Contents.HasFlag(WrRadioContent.ListeningHistory))
            await new ListeningHistoryStore(_root).ImportAsync(plan.HistoryEntries, replace: true, cancellationToken);
        return plan;
    }

    async Task CommitAsync(ConfigurationStore store, WrRadioImportPlan plan, CancellationToken cancellationToken)
    {
        Log("Package.Commit", "Staging import plan.");
        var stage = await StageLocalMediaAsync(plan, cancellationToken);
        try
        {
            // Final destinations are unique per package, so this commit cannot replace a prior imported file.
            FinalizeStagedMedia(stage);
            await store.SaveAsync(plan.ProposedConfiguration, cancellationToken);
            Log("Package.Commit", "Committed configuration atomically.");
        }
        catch (Exception error)
        {
            foreach (var item in stage) TryDelete(item.TemporaryPath);
            foreach (var item in stage) TryDelete(item.FinalPath);
            Log("Package.Commit", $"Failed: {error.GetType().Name}: {error.Message}");
            throw;
        }
    }

    async Task<List<(string TemporaryPath, string FinalPath)>> StageLocalMediaAsync(WrRadioImportPlan plan, CancellationToken cancellationToken)
    {
        var package = plan.Package;
        if (package.Manifest.MediaAssets.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(package.SourcePath)) throw new InvalidDataException("Embedded media requires a package file path.");
        Directory.CreateDirectory(ImportedMediaDirectory);
        var result = new List<(string, string)>();
        using var archive = ZipFile.OpenRead(package.SourcePath);
        foreach (var asset in package.Manifest.MediaAssets)
        {
            if (!plan.SourceIds.TryGetValue(asset.SourceId, out var importedSourceId)) continue;
            var entry = archive.GetEntry(asset.ArchivePath) ?? throw new InvalidDataException($"Embedded media '{asset.FileName}' is missing.");
            ValidateEntry(entry);
            var extension = Path.GetExtension(asset.FileName);
            var destination = Path.Combine(ImportedMediaDirectory,
                importedSourceId.ToString("N") + "-" + package.Manifest.PackageId.ToString("N") + extension);
            var temporary = destination + ".incoming-" + Guid.NewGuid().ToString("N");
            await using var input = entry.Open();
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, cancellationToken);
            var proposedSource = plan.ProposedConfiguration.MusicLibrary.Sources.FirstOrDefault(source => source.Id == importedSourceId);
            if (proposedSource is not null) proposedSource.Source = destination;
            result.Add((temporary, destination));
        }
        Log("Package.LocalMedia", $"Staged {result.Count} embedded media file(s).");
        return result;
    }

    static void FinalizeStagedMedia(IEnumerable<(string TemporaryPath, string FinalPath)> staged)
    {
        foreach (var (temporary, final) in staged)
        {
            File.Move(temporary, final, overwrite: false);
        }
    }

    async Task AddLocalMediaAsync(ZipArchive archive, WrRadioPackage package, CancellationToken cancellationToken)
    {
        foreach (var asset in package.Manifest.MediaAssets)
        {
            var source = package.Sources.Single(source => source.Id == asset.SourceId);
            var entry = archive.CreateEntry(asset.ArchivePath, CompressionLevel.Optimal);
            await using var output = entry.Open();
            await using var input = new FileStream(source.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    static void PrepareLocalMediaManifest(WrRadioPackage package)
    {
        package.Manifest.MediaAssets.Clear();
        foreach (var source in package.Sources.Where(IsLocalMedia))
        {
            if (!File.Exists(source.Source)) continue;
            var fileName = SanitizeFileName(Path.GetFileName(source.Source));
            var info = new FileInfo(source.Source);
            if (info.Length > MaximumArchiveEntryBytes) throw new InvalidDataException($"Local media '{fileName}' exceeds the package safety limit.");
            package.Manifest.MediaAssets.Add(new WrRadioMediaAsset
            {
                SourceId = source.Id,
                ArchivePath = $"assets/media/{source.Id:N}-{fileName}",
                FileName = fileName,
                Length = info.Length
            });
        }
    }

    static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > MaximumEntries) throw new InvalidDataException("The package has an unsafe number of archive entries.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateEntry(entry);
            total = checked(total + entry.Length);
            if (total > MaximumArchiveBytes) throw new InvalidDataException("The package expands beyond the supported safety limit.");
        }
    }

    static void ValidateEntry(ZipArchiveEntry entry)
    {
        if (!IsSafeArchivePath(entry.FullName)) throw new InvalidDataException("The package contains an unsafe archive path.");
        if (entry.Length > MaximumArchiveEntryBytes) throw new InvalidDataException($"The archive entry '{entry.FullName}' is too large.");
        if (entry.Length > 0 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 250)
            throw new InvalidDataException($"The archive entry '{entry.FullName}' has an unsafe compression ratio.");
    }

    static bool IsSafeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\')) return false;
        var segments = path.Split('/', StringSplitOptions.None);
        return segments.Length > 0 && segments.All(segment => !string.IsNullOrWhiteSpace(segment) &&
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && segment.All(character => character >= ' '));
    }

    void ValidatePackage(WrRadioPackage package, ZipArchive archive)
    {
        if (package.Manifest.PackageId == Guid.Empty || string.IsNullOrWhiteSpace(package.Manifest.PackageName))
            throw new InvalidDataException("The package manifest is missing a valid identity or name.");
        if (package.Stations.Any(station => station.PlaylistEntries is null) ||
            package.Macros.Any(macro => macro.Actions is null || macro.ReleaseActions is null || macro.OffActions is null))
            throw new InvalidDataException("The package contains an invalid collection.");
        if (package.Sources.Count > MaximumObjectsPerCategory || package.Songs.Count > MaximumObjectsPerCategory ||
            package.Stations.Count > MaximumObjectsPerCategory || package.Macros.Count > MaximumObjectsPerCategory ||
            package.Manifest.MediaAssets.Count > MaximumObjectsPerCategory || package.ListeningHistory.Count > 1_000_000)
            throw new InvalidDataException("The package contains an unreasonable number of configuration objects.");
        EnsureDistinct(package.Sources.Select(source => source.Id), "media source");
        EnsureDistinct(package.Songs.Select(song => song.Id), "library song");
        EnsureDistinct(package.Stations.Select(station => station.Id), "station");
        EnsureDistinct(package.Macros.Select(macro => macro.Id), "macro");
        EnsureDistinct(package.ListeningHistory.Select(entry => entry.Id), "listening history entry");
        if (package.ListeningHistory.Any(entry => entry.Id == Guid.Empty || entry.SessionId == Guid.Empty ||
            !Enum.IsDefined(entry.Type) || !double.IsFinite(entry.AudibleSeconds) || entry.AudibleSeconds < 0 ||
            (entry.Type == ListeningEntryType.Interval
                ? entry.StartedAt is null || entry.Timestamp <= entry.StartedAt ||
                  entry.AudibleSeconds > (entry.Timestamp - entry.StartedAt.Value).TotalSeconds + 2
                : entry.StartedAt is not null || entry.AudibleSeconds != 0)))
            throw new InvalidDataException("The package contains invalid Listening History.");
        var sourceIds = package.Sources.Select(source => source.Id).ToHashSet();
        var songIds = package.Songs.Select(song => song.Id).ToHashSet();
        var stationIds = package.Stations.Select(station => station.Id).ToHashSet();
        if (package.Sources.Any(source => source.Id == Guid.Empty || string.IsNullOrWhiteSpace(source.Source))) throw new InvalidDataException("The package contains an invalid media source.");
        if (package.Songs.Any(song => song.Id == Guid.Empty || song.SourceId == Guid.Empty || !sourceIds.Contains(song.SourceId))) throw new InvalidDataException("A library song references a missing media source.");
        if (package.Stations.Any(station => station.Id == Guid.Empty || station.PlaylistEntries.Any(entry => entry.Id == Guid.Empty || entry.SongId == Guid.Empty || !songIds.Contains(entry.SongId))))
            throw new InvalidDataException("A station playlist references a missing library song.");
        foreach (var station in package.Stations) EnsureDistinct(station.PlaylistEntries.Select(entry => entry.Id), "station playlist entry");
        if (package.Macros.Any(macro => macro.Id == Guid.Empty || AllActions(macro).Any(action => action.StationId is { } id && !stationIds.Contains(id))))
            throw new InvalidDataException("A macro references a missing station.");
        foreach (var asset in package.Manifest.MediaAssets)
        {
            var entry = archive.GetEntry(asset.ArchivePath);
            if (!sourceIds.Contains(asset.SourceId) || asset.Length < 0 || !IsSafeArchivePath(asset.ArchivePath) ||
                !string.Equals(Path.GetFileName(asset.FileName), asset.FileName, StringComparison.Ordinal) ||
                asset.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry is null || entry.Length != asset.Length)
                throw new InvalidDataException("The package contains an invalid embedded-media reference.");
        }
        EnsureDistinct(package.Manifest.MediaAssets.Select(asset => asset.SourceId), "embedded-media source");
    }

    static void ValidateManifestAndRequiredFiles(WrRadioPackageManifest manifest, ZipArchive archive)
    {
        if (!string.Equals(manifest.Format, WrRadioPackageManifest.FormatIdentifier, StringComparison.Ordinal))
            throw new InvalidDataException("This file is not a WARDOGS Radio package.");
        if (manifest.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException("This package was created using a newer WARDOGS Radio package format and cannot safely be imported.");
        if (manifest.FormatVersion < 0 || !Enum.IsDefined(manifest.PackageType) ||
            (manifest.Contents & ~WrRadioContent.All) != WrRadioContent.None || manifest.MediaAssets is null ||
            manifest.CreatedUtc == default || string.IsNullOrWhiteSpace(manifest.ApplicationVersion))
            throw new InvalidDataException("The package manifest contains unsupported or invalid values.");
        foreach (var requiredPath in new[]
        {
            "configuration/stations.json", "configuration/macros.json", "library/sources.json", "library/songs.json", "metadata/export-info.json"
        })
            if (archive.GetEntry(requiredPath) is null)
                throw new InvalidDataException($"The package is missing required file '{requiredPath}'.");
        if ((manifest.Contents & (WrRadioContent.GeneralSettings | WrRadioContent.PlaybackSettings | WrRadioContent.AudioRoutingSettings)) != WrRadioContent.None &&
            archive.GetEntry("configuration/settings.json") is null)
            throw new InvalidDataException("The package is missing required file 'configuration/settings.json'.");
        if (manifest.Contents.HasFlag(WrRadioContent.ListeningHistory) && archive.GetEntry("listening/history.json") is null)
            throw new InvalidDataException("The package is missing required file 'listening/history.json'.");
    }

    WrRadioPackage Migrate(WrRadioPackage package)
    {
        while (package.Manifest.FormatVersion < CurrentFormatVersion)
        {
            var migration = _migrations.SingleOrDefault(candidate => candidate.FromVersion == package.Manifest.FormatVersion);
            if (migration is null) throw new InvalidDataException($"Package format version {package.Manifest.FormatVersion} cannot be migrated safely.");
            Log("Package.Migrate", $"Migrating v{migration.FromVersion} to v{migration.ToVersion}.");
            package = migration.Migrate(package);
            if (package.Manifest.FormatVersion != migration.ToVersion) package.Manifest.FormatVersion = migration.ToVersion;
        }
        return package;
    }

    static Guid ImportSource(AppConfiguration target, MediaSource incoming, LibraryConflictResolution resolution, WrRadioImportPlan plan)
    {
        var sameId = target.MusicLibrary.Sources.FirstOrDefault(source => source.Id == incoming.Id);
        var sameIdentity = target.MusicLibrary.Sources.FirstOrDefault(source => SameSourceIdentity(source, incoming));
        if (sameId is null && sameIdentity is null) { target.MusicLibrary.Sources.Add(Clone(incoming)); return incoming.Id; }
        var existing = sameId ?? sameIdentity!;
        if (SourceEqual(existing, incoming)) return existing.Id;
        plan.Conflicts.Add(new WrRadioConflict("Media source", incoming.Id, incoming.Name, "The same source identity has different metadata."));
        if (resolution == LibraryConflictResolution.ReplaceExisting)
        {
            var replacement = Clone(incoming); replacement.Id = existing.Id;
            Replace(target.MusicLibrary.Sources, existing.Id, replacement); return existing.Id;
        }
        if (resolution == LibraryConflictResolution.KeepExisting) return existing.Id;
        // A canonical library cannot keep two separate MediaSource records for the same playable identity.
        // Keep the existing source while importing any distinct cue as a copy against that source.
        if (sameIdentity is not null) return sameIdentity.Id;
        var copy = Clone(incoming); copy.Id = Guid.NewGuid(); copy.Name = ImportedName(copy.Name, target.MusicLibrary.Sources.Select(source => source.Name));
        target.MusicLibrary.Sources.Add(copy); return copy.Id;
    }

    static Guid ImportSong(AppConfiguration target, LibrarySong incoming, Guid sourceId, LibraryConflictResolution resolution, WrRadioImportPlan plan)
    {
        var candidate = Clone(incoming); candidate.SourceId = sourceId;
        var sameId = target.MusicLibrary.Songs.FirstOrDefault(song => song.Id == incoming.Id);
        var sameIdentity = target.MusicLibrary.Songs.FirstOrDefault(song => SongEqual(song, candidate));
        if (sameId is null && sameIdentity is null) { target.MusicLibrary.Songs.Add(candidate); return candidate.Id; }
        var existing = sameId ?? sameIdentity!;
        if (SongEqual(existing, candidate)) return existing.Id;
        plan.Conflicts.Add(new WrRadioConflict("Library song", incoming.Id, incoming.Name, "The same song identity has different cue metadata."));
        if (resolution == LibraryConflictResolution.ReplaceExisting) { Replace(target.MusicLibrary.Songs, existing.Id, candidate); return existing.Id; }
        if (resolution == LibraryConflictResolution.KeepExisting) return existing.Id;
        candidate.Id = Guid.NewGuid(); candidate.Name = ImportedName(candidate.Name, target.MusicLibrary.Songs.Select(song => song.Name));
        target.MusicLibrary.Songs.Add(candidate); return candidate.Id;
    }

    static Guid ImportStation(AppConfiguration target, Station incoming, IReadOnlyDictionary<Guid, Guid> songIds,
        StationConflictResolution resolution, WrRadioImportPlan plan, bool includeKeyboard, bool includeController)
    {
        var candidate = Clone(incoming);
        candidate.PlaylistEntries = candidate.PlaylistEntries.Select(entry => new StationPlaylistEntry { Id = entry.Id, SongId = songIds[entry.SongId] }).ToList();
        if (!includeKeyboard) candidate.Hotkey = null;
        if (!includeController) candidate.ControllerBinding = null;
        var sameId = target.Profile.Stations.FirstOrDefault(station => station.Id == incoming.Id);
        if (sameId is null)
        {
            if (target.Profile.Stations.Any(station => string.Equals(station.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)))
            {
                plan.Conflicts.Add(new WrRadioConflict("Station", incoming.Id, incoming.Name, "A different station already has this name."));
                if (resolution == StationConflictResolution.Skip || resolution == StationConflictResolution.KeepExisting) return Guid.Empty;
                if (resolution == StationConflictResolution.ReplaceExisting)
                {
                    var named = target.Profile.Stations.First(station => string.Equals(station.Name, candidate.Name, StringComparison.OrdinalIgnoreCase));
                    candidate.Id = named.Id; Replace(target.Profile.Stations, named.Id, candidate); return named.Id;
                }
                candidate.Id = Guid.NewGuid(); RegenerateEntryIds(candidate); candidate.Name = ImportedName(candidate.Name, target.Profile.Stations.Select(station => station.Name));
            }
            target.Profile.Stations.Add(candidate); return candidate.Id;
        }
        if (StationEqual(sameId, candidate)) return sameId.Id;
        plan.Conflicts.Add(new WrRadioConflict("Station", incoming.Id, incoming.Name, "The same station identity has different content."));
        if (resolution == StationConflictResolution.Skip || resolution == StationConflictResolution.KeepExisting) return sameId.Id;
        if (resolution == StationConflictResolution.ReplaceExisting) { Replace(target.Profile.Stations, sameId.Id, candidate); return sameId.Id; }
        candidate.Id = Guid.NewGuid(); RegenerateEntryIds(candidate); candidate.Name = ImportedName(candidate.Name, target.Profile.Stations.Select(station => station.Name));
        target.Profile.Stations.Add(candidate); return candidate.Id;
    }

    static Guid ImportMacro(AppConfiguration target, RadioMacro incoming, IReadOnlyDictionary<Guid, Guid> stationIds,
        LibraryConflictResolution resolution, WrRadioImportPlan plan, bool includeKeyboard, bool includeController)
    {
        var candidate = Clone(incoming);
        foreach (var action in AllActions(candidate))
            if (action.StationId is { } id && stationIds.TryGetValue(id, out var mapped))
            {
                if (mapped == Guid.Empty) throw new InvalidOperationException($"Macro '{incoming.Name}' requires a station that was skipped during import.");
                action.StationId = mapped;
            }
        if (!includeKeyboard) { candidate.KeyboardBindings = []; candidate.Hotkey = null; }
        if (!includeController) candidate.ControllerBindings = [];
        var sameId = target.Profile.Macros.FirstOrDefault(macro => macro.Id == incoming.Id);
        if (sameId is null) { target.Profile.Macros.Add(candidate); return candidate.Id; }
        if (MacroEqual(sameId, candidate)) return sameId.Id;
        plan.Conflicts.Add(new WrRadioConflict("Macro", incoming.Id, incoming.Name, "The same macro identity has different actions or bindings."));
        if (resolution == LibraryConflictResolution.ReplaceExisting) { Replace(target.Profile.Macros, sameId.Id, candidate); return sameId.Id; }
        if (resolution == LibraryConflictResolution.KeepExisting) return sameId.Id;
        candidate.Id = Guid.NewGuid(); candidate.Name = ImportedName(candidate.Name, target.Profile.Macros.Select(macro => macro.Name));
        target.Profile.Macros.Add(candidate); return candidate.Id;
    }

    static void AddHardwareWarnings(WrRadioSettings? settings, WrRadioImportOptions options, List<string> warnings)
    {
        if (settings is null || !options.Contents.HasFlag(WrRadioContent.AudioRoutingSettings)) return;
        AddDeviceWarning(settings.MonitorDeviceId, "headphone/speaker", options.AvailableAudioDeviceIds, warnings);
        AddDeviceWarning(settings.MicrophoneDeviceId, "microphone", options.AvailableAudioDeviceIds, warnings);
        if (!string.IsNullOrWhiteSpace(settings.MpvAudioDeviceName) || !string.IsNullOrWhiteSpace(settings.GameMpvAudioDeviceName)) warnings.Add("The package includes audio-output settings. Verify the target devices before playback.");
    }

    static void AddDeviceWarning(string? deviceId, string label, ISet<string> availableDevices, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        if (availableDevices.Count > 0 && !availableDevices.Contains(deviceId))
            warnings.Add($"The package references a {label} device that is not currently available. Its identity was retained without automatic reassignment.");
        else
            warnings.Add($"The package includes a {label} device identity. It is retained without automatic reassignment.");
    }

    static void AddControllerWarnings(IEnumerable<Station> stations, IEnumerable<RadioMacro> macros, WrRadioImportOptions options, List<string> warnings)
    {
        if (!options.Contents.HasFlag(WrRadioContent.ControllerBindings)) return;
        var bindings = stations.Select(station => station.ControllerBinding)
            .Concat(macros.SelectMany(macro => macro.ControllerBindings ?? []))
            .Where(binding => !string.IsNullOrWhiteSpace(binding)).Cast<string>();
        foreach (var deviceId in bindings.Select(TryDecodeControllerDeviceId).Where(id => id is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (options.AvailableControllerDeviceIds.Count > 0 && !options.AvailableControllerDeviceIds.Contains(deviceId))
                warnings.Add("The package references a controller that is not currently connected. Its binding was retained without automatic reassignment.");
        }
    }

    static string? TryDecodeControllerDeviceId(string binding)
    {
        if (!binding.StartsWith("controller:", StringComparison.Ordinal)) return null;
        var parts = binding.Split(':', 3);
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[2])) return null;
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])); }
        catch (FormatException) { return null; }
    }

    static bool IsLocalMedia(MediaSource source)
    {
        if (!source.ProviderId.Equals("mpv", StringComparison.OrdinalIgnoreCase)) return false;
        return !Uri.TryCreate(source.Source, UriKind.Absolute, out var uri) || uri.IsFile;
    }
    static IEnumerable<RadioAction> AllActions(RadioMacro macro) => macro.Actions.Concat(macro.ReleaseActions).Concat(macro.OffActions);
    static List<T> Pick<T>(IEnumerable<T> all, IReadOnlyCollection<Guid> ids, bool include) where T : class => !include ? [] :
        (ids.Count == 0 ? all : all.Where(item => GetId(item) is { } id && ids.Contains(id))).ToList();
    static Guid? GetId<T>(T item) where T : class => item switch { Station station => station.Id, LibrarySong song => song.Id, MediaSource source => source.Id, RadioMacro macro => macro.Id, _ => null };
    static void EnsureDistinct(IEnumerable<Guid> ids, string type) { var values = ids.ToList(); if (values.Any(id => id == Guid.Empty) || values.Distinct().Count() != values.Count) throw new InvalidDataException($"The package contains duplicate or invalid {type} IDs."); }
    static bool SameSourceIdentity(MediaSource a, MediaSource b) => a.ProviderId.Equals(b.ProviderId, StringComparison.OrdinalIgnoreCase) &&
        MusicLibraryService.NormalizeSource(a.ProviderId, a.Source).Equals(MusicLibraryService.NormalizeSource(b.ProviderId, b.Source), StringComparison.OrdinalIgnoreCase);
    static bool SourceEqual(MediaSource a, MediaSource b) => SameSourceIdentity(a, b) && a.Name == b.Name && a.DurationSeconds == b.DurationSeconds;
    static bool SongEqual(LibrarySong a, LibrarySong b) => a.SourceId == b.SourceId && a.Name == b.Name && a.Artist == b.Artist && a.StartSeconds == b.StartSeconds && a.EndSeconds == b.EndSeconds;
    static bool StationEqual(Station a, Station b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions);
    static bool MacroEqual(RadioMacro a, RadioMacro b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions);
    static void Replace<T>(List<T> collection, Guid id, T value) where T : class => collection[collection.FindIndex(item => GetId(item) == id)] = value;
    static void RegenerateEntryIds(Station station) { foreach (var entry in station.PlaylistEntries) entry.Id = Guid.NewGuid(); }
    static string ImportedName(string name, IEnumerable<string> names) { var baseName = string.IsNullOrWhiteSpace(name) ? "Imported" : name.Trim(); var candidate = baseName + " (Imported)"; var i = 2; var taken = names.ToHashSet(StringComparer.OrdinalIgnoreCase); while (taken.Contains(candidate)) candidate = baseName + $" (Imported {i++})"; return candidate; }
    static string SafePackageName(string? name, WrRadioPackageType type) => string.IsNullOrWhiteSpace(name) ? type == WrRadioPackageType.FullBackup ? "WARDOGS Radio Full Backup" : "WARDOGS Radio Export" : name.Trim();
    static string EnsureExtension(string path) => path.EndsWith(".wradio", StringComparison.OrdinalIgnoreCase) ? path : path + ".wradio";
    static string NextAvailablePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name}-{suffix}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
    static string SanitizeFileName(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)).Trim().TrimEnd('.');
    static string AppVersion() => typeof(WrRadioPackageService).Assembly.GetName().Version?.ToString() ?? "development";
    void Log(string category, string message)
    {
        try
        {
            var directory = Path.Combine(_root, "Diagnostics");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "package.log"), $"{DateTimeOffset.UtcNow:o} {category} {message}{Environment.NewLine}");
        }
        catch { /* Diagnostics must never change package safety behavior. */ }
    }
    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    static T Clone<T>(T source) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;
    static List<T> Clone<T>(IEnumerable<T> source) => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;
    static WrRadioPackageSummary Summarize(WrRadioPackage package) => new(package.Manifest.PackageName, package.Manifest.PackageType,
        package.Stations.Count, package.Songs.Count, package.Sources.Count, package.Macros.Count, package.Manifest.MediaAssets.Count,
        package.Manifest.MediaAssets.Sum(asset => asset.Length), package.Manifest.Contents);
    static async Task WriteJsonAsync<T>(ZipArchive archive, string path, T value, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }
    static async Task<T?> ReadJsonAsync<T>(ZipArchive archive, string path, bool required, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(path);
        if (entry is null) { if (required) throw new InvalidDataException($"The package is missing required file '{path}'."); return default; }
        ValidateEntry(entry);
        await using var stream = entry.Open();
        try { return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken); }
        catch (JsonException error) { throw new InvalidDataException($"The package file '{path}' contains malformed JSON.", error); }
    }
}
