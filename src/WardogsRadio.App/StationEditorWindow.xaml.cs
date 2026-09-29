using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WardogsRadio.Core;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

public partial class StationEditorWindow : Window
{
    sealed class PlaylistItem(string path, StationSong? song = null)
    {
        public string Path { get; } = path;
        public StationSong Song { get; } = song ?? new StationSong { Source = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
        public string Name => Song.Name;
    }

    readonly Station _station;
    readonly List<PlaylistItem> _playlist = [];
    string _iconId;
    string _accentColor;
    bool _shuffle;
    int? _shuffleSeed;
    bool _usingSavedSongs;
    readonly MusicLibrary? _library;
    readonly bool _allowPreview;
    bool _initializing = true;
    bool _synchronizingSourceControls;
    public Station? Result { get; private set; }
    public MediaSearchResult? SearchResult { get; private set; }

    public StationEditorWindow(Station? station = null, MusicLibrary? library = null, bool allowPreview = true)
    {
        InitializeComponent();
        _station = station ?? new Station();
        _library = library;
        _allowPreview = allowPreview;
        _iconId = _station.IconId;
        _accentColor = AccentColorPickerWindow.NormalizeColor(_station.AccentColor);
        Heading.Text = station is null ? "CREATE RADIO STATION" : "EDIT RADIO STATION";
        NameBox.Text = station is null ? "" : station.Name;
        SourceBox.Text = _station.Source;
        _usingSavedSongs = _station.PlaylistSongs?.Count > 0;
        _playlist.AddRange(_usingSavedSongs
            ? _station.PlaylistSongs.Select(song => new PlaylistItem(song.Source, song))
            : (_station.PlaylistFiles ?? []).Select(path => new PlaylistItem(path)));
        var existingLocalSource = _station.ProviderId == "mpv" && !string.IsNullOrWhiteSpace(_station.Source) &&
            !(Uri.TryCreate(_station.Source, UriKind.Absolute, out var existingLink) && existingLink.Scheme is "http" or "https");
        if (_playlist.Count == 0 && existingLocalSource && !Directory.Exists(_station.Source))
            _playlist.Add(new PlaylistItem(_station.Source));
        if (_playlist.Count == 0 && _station.ProviderId == "mpv" && Directory.Exists(_station.Source))
        {
            try { _playlist.AddRange(LocalMediaPlaylist.FromDirectory(_station.Source, false).Select(path => new PlaylistItem(path))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { ValidationText.Text = error.Message; }
        }
        _shuffle = _station.Shuffle;
        _shuffleSeed = _station.ShuffleSeed ?? (_shuffle ? Random.Shared.Next() : null);
        RefreshPlaylist();
        UpdateIcon();
        UpdateAccent();
        var localSource = _playlist.Count > 0 || existingLocalSource || station is null;
        Select(ProviderBox, StationSourceSelection.ForStation(_station.ProviderId, localSource));
        Select(RepeatModeBox, _station.EffectiveRepeatMode.ToString());
        Select(ModeBox, _station.ModeOverride?.ToString() ?? "default");
        StationVolume.Value = _station.Volume;
        GameStationVolume.Value = _station.GameVolume;
        DraftCheck.IsChecked = station is not null && string.IsNullOrWhiteSpace(station.Source) && _playlist.Count == 0;
        if (StationSourceSelection.IsLocal(ProviderChoice())) LocalSourceChoice.IsChecked = true;
        else LinkSourceChoice.IsChecked = true;
        _initializing = false;
        UpdateProviderUi();
    }

    static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x =>
            string.Equals(x.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) ?? box.Items[0];

    void UpdateIcon()
    {
        var icon = IconCatalog.Get(_iconId);
        IconPreview.Data = icon.Shape;
        IconName.Text = icon.Name;
    }

    void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var picker = new IconPickerWindow(_iconId) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedIconId is not { } selected) return;
        _iconId = selected;
        UpdateIcon();
    }

    void UpdateAccent()
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_accentColor)!);
        AccentSwatch.Background = brush;
        IconPreview.Fill = brush;
        AccentLabel.Text = _accentColor.ToUpperInvariant();
    }

    void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        var picker = new AccentColorPickerWindow(_accentColor) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedColor is not { } color) return;
        _accentColor = color;
        UpdateAccent();
    }

    string ProviderChoice() => (ProviderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? StationSourceSelection.LocalMpv;
    string Provider() => StationSourceSelection.ProviderId(ProviderChoice());

    void ProviderTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string choice) return;
        Select(ProviderBox, choice);
    }

    void UpdateProviderTiles()
    {
        var current = ProviderChoice();
        foreach (var tile in new[] { LocalProviderTile, RadioProviderTile, YouTubeProviderTile,
                     ExternalProviderTile, SoundCloudProviderTile, AppleProviderTile })
        {
            var selected = tile.Tag?.ToString() == current;
            tile.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(
                selected ? (byte)51 : (byte)32, selected ? (byte)69 : (byte)42, selected ? (byte)46 : (byte)39));
            tile.BorderBrush = selected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(159, 182, 114))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 65, 60));
            tile.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
        }
    }

    void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _synchronizingSourceControls || ProviderHint is null) return;
        _synchronizingSourceControls = true;
        try
        {
            if (StationSourceSelection.IsLocal(ProviderChoice())) LocalSourceChoice.IsChecked = true;
            else LinkSourceChoice.IsChecked = true;
        }
        finally { _synchronizingSourceControls = false; }
        UpdateProviderUi();
    }

    void UpdateProviderUi()
    {
        if (Provider() != "youtube") SearchResult = null;
        UpdateProviderTiles();
        ProviderHint.Text = Provider() switch
        {
            "youtube" => "Paste a YouTube video or playlist link.",
            "soundcloud" => "SoundCloud account access is not connected. You can save a draft until access is available.",
            "applemusic" => "Apple Music account access is not connected. You can save a draft until access is available.",
            "external-audio" => "Select a running music app from the Stations page.",
            _ => StationSourceSelection.IsLocal(ProviderChoice()) ? "Add local audio files to this station's playlist." : "Paste a direct http:// or https:// music stream link."
        };
        ValidationText.Text = "";
        LocalSourceChoice.IsEnabled = true;
        RepeatModeBox.IsEnabled = Provider() is "mpv" or "youtube";
        LinkSourceLabel.Text = Provider() == "external-audio" ? "External media session" : "Music link (https://...)";
        SearchYouTubeButton.Visibility = Provider() == "youtube" ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourcePanels();
    }

    void SearchYouTube_Click(object sender, RoutedEventArgs e)
    {
        var search = new YouTubeSearchWindow(_allowPreview, _library) { Owner = this };
        if (search.ShowDialog() != true || search.SelectedResult is not { } result) return;
        SearchResult = result;
        if (!string.Equals(SourceBox.Text, result.CanonicalUrl, StringComparison.OrdinalIgnoreCase))
        {
            _playlist.Clear();
            _usingSavedSongs = false;
            RefreshPlaylist();
        }
        SourceBox.Text = result.CanonicalUrl;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = result.Title;
        ValidationText.Text = result.Type == MediaSearchResultType.Playlist
            ? "PLAYLIST SELECTED · A playlist plays as a collection and does not expose one cue timeline."
            : "VIDEO SELECTED · Save to add it through the normal Library source path.";
    }

    void SourceChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _synchronizingSourceControls) return;
        var desired = StationSourceSelection.ForSourceType(LocalSourceChoice.IsChecked == true, ProviderChoice());
        _synchronizingSourceControls = true;
        try { if (ProviderChoice() != desired) Select(ProviderBox, desired); }
        finally { _synchronizingSourceControls = false; }
        UpdateProviderUi();
    }

    void UpdateSourcePanels()
    {
        var local = LocalSourceChoice.IsChecked == true;
        PlaylistPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        OrderPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        LinkSourcePanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        ValidationText.Text = "";
    }

    void SourceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchResult is { } selected && !string.Equals(
            MusicLibraryService.NormalizeSource("youtube", SourceBox.Text), selected.CanonicalUrl,
            StringComparison.OrdinalIgnoreCase)) SearchResult = null;
        if (ValidationText is not null) ValidationText.Text = "";
    }

    void StationVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StationVolumeText is not null)
            StationVolumeText.Text = $"{Math.Round(e.NewValue * 100):0}% · Multiplied by headset master level";
    }

    void GameStationVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GameStationVolumeText is not null)
            GameStationVolumeText.Text = $"{Math.Round(e.NewValue * 100):0}% · Multiplied by game master level when connected";
    }

    void RefreshPlaylist(int selectedIndex = -1)
    {
        PlaylistList.ItemsSource = null;
        PlaylistList.ItemsSource = DisplayPlaylist();
        PlaylistList.SelectedIndex = selectedIndex;
        PlaylistCount.Text = $"{_playlist.Count} {(_playlist.Count == 1 ? "track" : "tracks")}";
        OrderButton.Tag = _shuffle ? null : "active";
        ShuffleButton.Tag = _shuffle ? "active" : null;
        OrderButton.Background = _shuffle ? System.Windows.Media.Brushes.Transparent : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 79, 49));
        ShuffleButton.Background = _shuffle ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 79, 49)) : System.Windows.Media.Brushes.Transparent;
        OrderHint.Text = _shuffle
            ? _playlist.Count <= 1 ? "Shuffle selected. Add more tracks to make the order change." : "Shuffle selected. Click SHUFFLE / RESHUFFLE again for a new order; the list shows the next playback order."
            : "Play in order selected. Tracks play from top to bottom.";
        if (ValidationText is not null) ValidationText.Text = "";
    }

    List<PlaylistItem> DisplayPlaylist()
    {
        var items = _playlist.ToList();
        if (_usingSavedSongs || !_shuffle || _shuffleSeed is not { } seed) return items;
        var random = new Random(seed);
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items;
    }

    void PlayInOrder_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = false;
        _shuffleSeed = null;
        RefreshPlaylist();
    }

    void ShuffleAgain_Click(object sender, RoutedEventArgs e)
    {
        var before = DisplayPlaylist();
        _shuffle = true;
        if (_usingSavedSongs)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                for (var index = _playlist.Count - 1; index > 0; index--)
                {
                    var other = Random.Shared.Next(index + 1);
                    (_playlist[index], _playlist[other]) = (_playlist[other], _playlist[index]);
                }
                if (_playlist.Count < 2 || !_playlist.SequenceEqual(before)) break;
            }
            _shuffleSeed = null;
            RefreshPlaylist();
            return;
        }
        for (var attempt = 0; attempt < 100; attempt++)
        {
            _shuffleSeed = Random.Shared.Next();
            if (_playlist.Count < 2 || !DisplayPlaylist().SequenceEqual(before)) break;
        }
        RefreshPlaylist();
    }

    void AddPlaylistFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Add audio tracks to this station playlist",
            Multiselect = true,
            Filter = "Audio and playlists|*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wav;*.wma;*.aiff;*.aif;*.alac;*.ape;*.wv;*.mka;*.m3u;*.m3u8|All files|*.*"
        };
        if (picker.ShowDialog(this) != true) return;
        _playlist.AddRange(picker.FileNames.Select(path => new PlaylistItem(path)));
        RefreshPlaylist(_playlist.Count - 1);
    }

    void AddPlaylistFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Add all supported audio files from a folder" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var files = LocalMediaPlaylist.FromDirectory(picker.FolderName, false);
            _playlist.AddRange(files.Select(path => new PlaylistItem(path)));
            RefreshPlaylist();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ValidationText.Text = "CHECK FOLDER · " + error.Message;
        }
    }

    void AddLibrarySongs_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null)
        {
            ValidationText.Text = "THE MUSIC LIBRARY IS NOT AVAILABLE YET.";
            return;
        }
        var picker = new LibrarySongPickerWindow(_library, Provider()) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedSongs.Count == 0) return;
        var sources = _library.Sources.ToDictionary(source => source.Id);
        foreach (var song in picker.SelectedSongs)
        {
            if (!sources.TryGetValue(song.SourceId, out var source)) continue;
            if (_playlist.Any(item => item.Song.Id == song.Id)) continue;
            _playlist.Add(new PlaylistItem(source.Source, new StationSong
            {
                Id = song.Id, Source = source.Source, Name = song.Name,
                StartSeconds = song.StartSeconds, EndSeconds = song.EndSeconds
            }));
        }
        if (!StationSourceSelection.IsLocal(ProviderChoice()) && string.IsNullOrWhiteSpace(SourceBox.Text) &&
            _playlist.FirstOrDefault() is { } first)
            SourceBox.Text = first.Path;
        _usingSavedSongs = true;
        RefreshPlaylist(_playlist.Count - 1);
    }

    void RemovePlaylistFiles_Click(object sender, RoutedEventArgs e)
    {
        var index = PlaylistList.SelectedItem is PlaylistItem selected ? _playlist.IndexOf(selected) : -1;
        if (index < 0) return;
        _playlist.RemoveAt(index);
        RefreshPlaylist();
    }

    void MovePlaylistUp_Click(object sender, RoutedEventArgs e) => MovePlaylist(-1);
    void MovePlaylistDown_Click(object sender, RoutedEventArgs e) => MovePlaylist(1);

    void MovePlaylist(int direction)
    {
        if (PlaylistList.SelectedItem is not PlaylistItem selected) return;
        var index = _playlist.IndexOf(selected);
        var destination = index + direction;
        if (index < 0 || destination < 0 || destination >= _playlist.Count) return;
        (_playlist[index], _playlist[destination]) = (_playlist[destination], _playlist[index]);
        RefreshPlaylist();
        PlaylistList.SelectedItem = selected;
    }

    void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _playlist.Clear();
        RefreshPlaylist();
    }

    async Task<SourceValidationResult> ValidateCurrentAsync()
    {
        var source = SourceBox.Text.Trim();
        if (Provider() == "mpv" && LocalSourceChoice.IsChecked == true)
        {
            try
            {
                var files = LocalMediaPlaylist.FromFiles(_playlist.Select(x => x.Path), false);
                return new(true, $"Playlist has {files.Count} audio tracks in the shown order.", files[0]);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                return new(false, error.Message);
            }
        }
        if (Provider() == "mpv" && !(Uri.TryCreate(source, UriKind.Absolute, out var link) && link.Scheme is "http" or "https"))
            return new(false, "Enter a full http:// or https:// music link, or switch to Local Playlist.");
        return Provider() switch
        {
            "youtube" => YouTubeUrl.Normalize(source),
            "soundcloud" => SoundCloudUrl.Normalize(source),
            "applemusic" => AppleMusicUrl.Normalize(source),
            "mpv" => await new MpvProvider(new MpvLocator()).ValidateSourceAsync(source),
            "external-audio" => string.IsNullOrWhiteSpace(source)
                ? new(false, "Select a running music app from Stations first.")
                : new(true, "External media session selected; playback still needs verification.", source),
            _ => new(false, "Choose a supported provider.")
        };
    }

    async void Validate_Click(object sender, RoutedEventArgs e)
    {
        var result = await ValidateCurrentAsync();
        ValidationText.Text = result.IsValid ? "SOURCE RECOGNIZED · " + result.Message : "CHECK SOURCE · " + result.Message;
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { ValidationText.Text = "ENTER A STATION NAME"; return; }
        var validation = await ValidateCurrentAsync();
        if (!validation.IsValid && DraftCheck.IsChecked != true)
        {
            ValidationText.Text = "CHECK SOURCE BEFORE SAVING · " + validation.Message;
            return;
        }
        _station.Name = NameBox.Text.Trim();
        var local = Provider() == "mpv" && LocalSourceChoice.IsChecked == true;
        var oldProvider = _station.ProviderId;
        var oldSource = _station.Source;
        _station.Source = local ? _playlist.FirstOrDefault()?.Path ?? "" :
            validation.IsValid ? validation.CanonicalSource ?? SourceBox.Text.Trim() : SourceBox.Text.Trim();
        _station.ProviderId = Provider();
        var savedOrder = _usingSavedSongs ? _playlist : DisplayPlaylist();
        _station.PlaylistFiles = local ? savedOrder.Select(x => x.Path).ToList() : [];
        if (local || _usingSavedSongs) _station.PlaylistSongs = savedOrder.Select(x => x.Song).ToList();
        else if (oldProvider != _station.ProviderId || !string.Equals(oldSource, _station.Source, StringComparison.OrdinalIgnoreCase))
            _station.PlaylistSongs = [];
        _station.IconId = _iconId;
        _station.Volume = StationVolume.Value;
        _station.GameVolume = GameStationVolume.Value;
        _station.AccentColor = _accentColor;
        _station.RepeatMode = (RepeatModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Off" => StationRepeatMode.Off,
            "Track" => StationRepeatMode.Track,
            _ => StationRepeatMode.Playlist
        };
        _station.Loop = _station.RepeatMode != StationRepeatMode.Off;
        _station.Shuffle = _shuffle;
        _station.ShuffleSeed = null; // The displayed song order is now the saved playback order.
        _station.ModeOverride = (ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "player" => PlaybackMode.Player,
            "radio" => PlaybackMode.Radio,
            "restarttrack" => PlaybackMode.RestartTrack,
            _ => null
        };
        _station.Description = _station.ProviderId switch
        {
            "soundcloud" => "SoundCloud station",
            "applemusic" => "Apple Music station",
            "youtube" => "YouTube station",
            "external-audio" => "External music app",
            _ => "Local or radio stream"
        };
        Result = _station;
        DialogResult = true;
    }
}
