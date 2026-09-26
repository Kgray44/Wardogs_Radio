using System.Windows;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class LibrarySongPickerWindow : Window
{
    sealed record SongChoice(LibrarySong Song, string Name, string Detail);
    public IReadOnlyList<LibrarySong> SelectedSongs { get; private set; } = [];

    public LibrarySongPickerWindow(MusicLibrary library, string providerId)
    {
        InitializeComponent();
        var sources = library.Sources.ToDictionary(source => source.Id);
        var compatible = library.Songs.Where(song => sources.TryGetValue(song.SourceId, out var source) &&
                string.Equals(source.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(song => song.Name, StringComparer.OrdinalIgnoreCase)
            .Select(song => new SongChoice(song, song.Name, Describe(song, sources[song.SourceId])))
            .ToList();
        Heading.Text = $"ADD {ProviderLabel(providerId)} CUES";
        Hint.Text = compatible.Count == 0
            ? "No compatible cues are in the Library yet. Add a source there first."
            : "Selected cues remain shared: edit them in the Library to update every station that uses them.";
        SongList.ItemsSource = compatible;
    }

    static string ProviderLabel(string providerId) => providerId == "youtube" ? "YOUTUBE" : "LOCAL";
    static string Describe(LibrarySong song, MediaSource source) => song.EndSeconds is { } end
        ? $"{DisplayTime(song.StartSeconds)}–{DisplayTime(end)} · {source.Name}"
        : song.StartSeconds > 0 ? $"from {DisplayTime(song.StartSeconds)} · {source.Name}" : $"full source · {source.Name}";

    static string DisplayTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedSongs = SongList.SelectedItems.OfType<SongChoice>().Select(choice => choice.Song).ToList();
        if (SelectedSongs.Count == 0) return;
        DialogResult = true;
    }
}
