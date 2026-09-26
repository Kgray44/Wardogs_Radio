using System.Windows;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class LibrarySongMembershipWindow : Window
{
    public sealed class StationChoice
    {
        public StationChoice(Station station, bool assigned)
        {
            Station = station;
            IsAssigned = assigned;
        }

        public Station Station { get; }
        public string Name => Station.Name;
        public string Detail => $"{(Station.ProviderId == "youtube" ? "YouTube" : "Local / mpv")} · {Station.PlaylistEntries.Count} assigned";
        public bool IsAssigned { get; set; }
    }

    public IReadOnlyList<StationChoice> Choices { get; private set; } = [];

    public LibrarySongMembershipWindow(LibrarySong song, MediaSource source, IEnumerable<Station> stations)
    {
        InitializeComponent();
        SongNameText.Text = song.Name;
        var compatible = stations.Where(station => string.Equals(station.ProviderId, source.ProviderId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(station => station.Order).Select(station => new StationChoice(station,
                station.PlaylistEntries.Any(entry => entry.SongId == song.Id))).ToList();
        HintText.Text = compatible.Count == 0
            ? "No compatible stations exist yet. Create a station using this source provider first."
            : "A checked station references this shared library cue. Editing the cue remains global.";
        Choices = compatible;
        StationList.ItemsSource = Choices;
    }

    void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
