using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public sealed record RadioIcon(string Id, string Name, string Category, Geometry Shape);

public static class IconCatalog
{
    static RadioIcon Icon(string id, string name, string category, string path) =>
        new(id, name, category, Geometry.Parse(path));

    public static IReadOnlyList<RadioIcon> All { get; } =
    [
        Icon("patrol", "Patrol", "Radio", "M 12,2 A 10,10 0 1 1 11.99,2 Z M 12,7 A 5,5 0 1 0 12.01,7 Z"),
        Icon("antenna", "Antenna", "Radio", "M 11,10 L 13,10 13,22 11,22 Z M 8,22 L 16,22 16,24 8,24 Z M 12,7 A 1,1 0 1 1 11.99,7 Z M 4,8 Q 12,-1 20,8 L 18,9 Q 12,3 6,9 Z"),
        Icon("wave", "Signal wave", "Radio", "M 0,13 L 4,13 7,7 11,18 15,3 19,13 24,13 24,16 17,16 15,11 11,24 7,14 5,16 0,16 Z"),
        Icon("music", "Music", "Audio", "M 9,4 L 21,2 21,17 A 4,4 0 1 1 19,13 L 19,7 11,9 11,20 A 4,4 0 1 1 9,16 Z"),
        Icon("headphones", "Headphones", "Audio", "M 2,13 A 10,10 0 0 1 22,13 L 22,20 17,20 17,13 20,13 A 8,8 0 0 0 4,13 L 7,13 7,20 2,20 Z"),
        Icon("aircraft", "Aircraft", "Aviation", "M 11,1 L 13,1 15,10 23,14 23,17 15,15 14,20 17,22 17,24 12,22 7,24 7,22 10,20 9,15 1,17 1,14 9,10 Z"),
        Icon("combat", "Combat", "Tactical", "M 12,2 L 23,22 1,22 Z"),
        Icon("flag", "Flag", "Tactical", "M 3,2 L 5,2 5,24 3,24 Z M 5,3 L 20,3 17,9 20,15 5,15 Z"),
        Icon("diamond", "Diamond", "Navigation", "M 12,1 L 23,12 12,23 1,12 Z"),
        Icon("star", "Star", "Navigation", "M 12,1 L 15,9 24,9 17,14 19,23 12,18 5,23 7,14 0,9 9,9 Z"),
        Icon("night", "Night", "Weather", "M 14,1 A 11,11 0 1 0 23,18 A 9,9 0 1 1 14,1 Z"),
        Icon("lightning", "Lightning", "Weather", "M 14,0 L 4,14 11,14 8,24 21,9 14,9 Z"),
        Icon("broadcast", "Broadcast", "Radio", "M 11,10 L 13,10 13,22 11,22 Z M 8,22 L 16,22 16,24 8,24 Z M 12,6 A 2,2 0 1 1 11.99,6 Z M 5,6 L 7,8 5,10 3,8 Z M 19,6 L 21,8 19,10 17,8 Z"),
        Icon("satellite", "Satellite", "Radio", "M 9,8 L 16,15 13,18 6,11 Z M 1,1 L 8,3 3,8 Z M 16,18 L 21,23 23,21 18,16 Z M 17,3 A 7,7 0 0 1 22,8 L 20,10 A 5,5 0 0 0 15,5 Z"),
        Icon("frequency", "Frequency", "Radio", "M 1,12 L 4,12 6,7 9,18 12,5 15,18 18,7 20,12 23,12 23,15 18,15 15,23 12,12 9,23 6,14 4,15 1,15 Z"),
        Icon("tower", "Radio tower", "Radio", "M 11,8 L 13,8 16,23 8,23 Z M 2,5 L 5,5 5,13 2,13 Z M 19,5 L 22,5 22,13 19,13 Z M 5,2 L 8,2 8,16 5,16 Z M 16,2 L 19,2 19,16 16,16 Z"),
        Icon("vinyl", "Vinyl record", "Audio", "M 12,1 A 11,11 0 1 1 11.99,1 Z M 12,9 A 3,3 0 1 0 12.01,9 Z"),
        Icon("cassette", "Cassette", "Audio", "M 2,4 L 22,4 22,20 2,20 Z M 5,8 L 19,8 19,14 5,14 Z M 7,15 L 10,15 10,18 7,18 Z M 14,15 L 17,15 17,18 14,18 Z"),
        Icon("microphone", "Microphone", "Audio", "M 9,3 A 3,3 0 0 1 15,3 L 15,12 A 3,3 0 0 1 9,12 Z M 5,10 L 7,10 A 5,5 0 0 0 17,10 L 19,10 A 7,7 0 0 1 13,19 L 13,22 17,22 17,24 7,24 7,22 11,22 11,19 A 7,7 0 0 1 5,10 Z"),
        Icon("speaker", "Speaker", "Audio", "M 2,9 L 7,9 13,4 13,20 7,15 2,15 Z M 16,7 L 18,5 A 10,10 0 0 1 18,19 L 16,17 A 7,7 0 0 0 16,7 Z"),
        Icon("equalizer", "Equalizer", "Audio", "M 2,4 L 5,4 5,20 2,20 Z M 8,9 L 11,9 11,20 8,20 Z M 14,2 L 17,2 17,20 14,20 Z M 20,7 L 23,7 23,20 20,20 Z"),
        Icon("drum", "Drum", "Audio", "M 3,7 L 21,7 21,18 3,18 Z M 3,4 L 21,4 21,6 3,6 Z M 3,19 L 21,19 21,21 3,21 Z"),
        Icon("guitar", "Guitar", "Audio", "M 16,1 L 19,1 19,4 22,4 22,7 19,7 19,11 15,15 12,14 9,17 6,20 2,20 1,17 4,14 7,13 10,10 9,7 13,3 16,3 Z"),
        Icon("play", "Play", "Audio", "M 5,2 L 22,12 5,22 Z"),
        Icon("pause", "Pause", "Audio", "M 4,3 L 10,3 10,21 4,21 Z M 14,3 L 20,3 20,21 14,21 Z"),
        Icon("playlist", "Playlist", "Audio", "M 2,4 L 15,4 15,6 2,6 Z M 2,10 L 15,10 15,12 2,12 Z M 2,16 L 11,16 11,18 2,18 Z M 17,9 L 23,9 23,19 A 3,3 0 1 1 21,16 L 21,12 19,13 19,21 A 3,3 0 1 1 17,18 Z"),
        Icon("helicopter", "Helicopter", "Aviation", "M 1,3 L 23,3 23,5 13,5 13,7 17,8 20,12 20,16 5,16 3,13 3,10 11,7 11,5 1,5 Z M 4,19 L 21,19 21,21 4,21 Z"),
        Icon("wing", "Wings", "Aviation", "M 12,3 L 15,8 23,4 20,11 15,13 12,22 9,13 4,11 1,4 9,8 Z"),
        Icon("parachute", "Parachute", "Aviation", "M 2,10 A 10,10 0 0 1 22,10 L 19,11 16,10 12,12 8,10 5,11 Z M 5,12 L 7,12 12,20 17,12 19,12 13,22 11,22 Z"),
        Icon("runway", "Runway", "Aviation", "M 9,1 L 15,1 21,23 3,23 Z M 11,4 L 13,4 13,8 11,8 Z M 11,11 L 13,11 13,15 11,15 Z M 11,18 L 13,18 13,22 11,22 Z"),
        Icon("shield", "Shield", "Tactical", "M 12,1 L 22,5 21,15 Q 19,21 12,24 Q 5,21 3,15 L 2,5 Z"),
        Icon("crosshair", "Crosshair", "Tactical", "M 11,0 L 13,0 13,4 A 8,8 0 0 1 20,11 L 24,11 24,13 20,13 A 8,8 0 0 1 13,20 L 13,24 11,24 11,20 A 8,8 0 0 1 4,13 L 0,13 0,11 4,11 A 8,8 0 0 1 11,4 Z M 12,10 A 2,2 0 1 0 12.01,10 Z"),
        Icon("radar", "Radar", "Tactical", "M 12,2 A 10,10 0 1 1 11.99,2 Z M 12,5 L 12,12 19,7 Z"),
        Icon("chevron", "Chevron", "Tactical", "M 12,2 L 23,13 20,16 12,8 4,16 1,13 Z M 12,9 L 23,20 20,23 12,15 4,23 1,20 Z"),
        Icon("compass", "Compass", "Navigation", "M 12,1 A 11,11 0 1 1 11.99,1 Z M 12,4 L 8,16 12,13 16,16 Z"),
        Icon("waypoint", "Waypoint", "Navigation", "M 12,1 A 8,8 0 0 1 20,9 Q 20,15 12,24 Q 4,15 4,9 A 8,8 0 0 1 12,1 Z M 12,6 A 3,3 0 1 0 12.01,6 Z"),
        Icon("route", "Route", "Navigation", "M 3,3 A 3,3 0 1 1 2.99,3 Z M 18,17 A 3,3 0 1 1 17.99,17 Z M 6,8 L 9,8 9,13 16,13 16,16 6,16 Z"),
        Icon("mountain", "Mountain", "Navigation", "M 1,22 L 10,3 15,13 18,8 24,22 Z"),
        Icon("sun", "Sun", "Weather", "M 12,7 A 5,5 0 1 1 11.99,7 Z M 11,0 L 13,0 13,4 11,4 Z M 11,20 L 13,20 13,24 11,24 Z M 0,11 L 4,11 4,13 0,13 Z M 20,11 L 24,11 24,13 20,13 Z"),
        Icon("cloud", "Cloud", "Weather", "M 7,20 A 6,6 0 0 1 5,8 A 8,8 0 0 1 20,10 A 5,5 0 0 1 19,20 Z"),
        Icon("rain", "Rain", "Weather", "M 7,17 A 6,6 0 0 1 5,5 A 8,8 0 0 1 20,7 A 5,5 0 0 1 19,17 Z M 5,19 L 8,19 6,24 3,24 Z M 12,19 L 15,19 13,24 10,24 Z M 19,19 L 22,19 20,24 17,24 Z"),
        Icon("snow", "Snow", "Weather", "M 11,1 L 13,1 13,23 11,23 Z M 2,11 L 22,11 22,13 2,13 Z M 4,4 L 6,3 21,18 19,20 Z M 19,3 L 21,4 6,20 4,18 Z"),
        Icon("fire", "Fire", "Weather", "M 12,1 Q 19,8 20,14 Q 20,23 12,23 Q 4,23 4,15 Q 5,9 10,7 Q 9,13 12,13 Q 15,11 12,1 Z"),
        Icon("anchor", "Anchor", "Navigation", "M 12,1 A 3,3 0 1 1 11.99,1 Z M 11,6 L 13,6 13,18 Q 18,19 20,13 L 23,13 Q 22,23 12,23 Q 2,23 1,13 L 4,13 Q 6,19 11,18 Z"),
        Icon("truck", "Truck", "Transport", "M 1,6 L 14,6 14,17 1,17 Z M 14,9 L 19,9 23,13 23,17 14,17 Z M 4,17 A 2,2 0 1 1 3.99,17 Z M 17,17 A 2,2 0 1 1 16.99,17 Z"),
        Icon("rocket", "Rocket", "Transport", "M 14,1 Q 22,1 23,2 Q 23,10 16,17 L 10,15 8,9 Z M 7,10 L 2,11 2,17 6,15 Z M 15,17 L 13,22 7,22 9,17 Z M 4,18 L 7,18 6,23 1,23 Z"),
        Icon("battery", "Battery", "Utility", "M 2,5 L 19,5 19,19 2,19 Z M 19,9 L 23,9 23,15 19,15 Z M 5,8 L 16,8 16,16 5,16 Z"),
        Icon("gear", "Gear", "Utility", "M 10,1 L 14,1 15,4 18,5 21,3 23,6 21,9 22,11 24,12 22,15 21,17 22,20 19,22 16,20 14,21 13,24 10,24 9,21 7,20 4,22 2,19 4,16 3,14 0,13 2,10 4,8 3,5 6,3 9,5 Z M 12,9 A 3,3 0 1 0 12.01,9 Z"),
        Icon("heart", "Heart", "Utility", "M 12,22 L 3,13 Q -1,8 3,4 Q 7,0 12,6 Q 17,0 21,4 Q 25,8 21,13 Z"),
        Icon("skull", "Skull", "Tactical", "M 12,1 A 10,10 0 0 1 22,11 Q 22,16 18,18 L 18,23 6,23 6,18 Q 2,16 2,11 A 10,10 0 0 1 12,1 Z M 7,11 A 2,2 0 1 0 7.01,11 Z M 17,11 A 2,2 0 1 0 17.01,11 Z")
    ];

    public static RadioIcon Get(string? id) => All.FirstOrDefault(x => x.Id == id) ?? All[0];
}

public sealed class IconGeometryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        IconCatalog.Get(value as string).Shape;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ChannelNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int order ? $"CH {order + 1:00}" : "CH --";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StationStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not Station station ? "Needs Setup" :
        !station.Enabled ? "Disabled" :
        string.IsNullOrWhiteSpace(station.Source) ? "Needs Setup" :
        station.Runtime.WasPlaying ? "Playing" :
        station.ProviderId is "soundcloud" or "applemusic" ? "Needs Account" :
        station.ProviderId == "mpv" && station.PlaylistFiles?.Count > 0 ?
            station.PlaylistFiles.All(File.Exists) ? "Playlist Set" : "Playlist Missing" :
        station.ProviderId == "mpv" && !(Uri.TryCreate(station.Source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") &&
            !File.Exists(station.Source) && !Directory.Exists(station.Source) ? "Source Missing" : "Source Set";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StationSourceSummaryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not Station station ? "" : station.PlaylistFiles?.Count > 0
            ? $"Local playlist · {station.PlaylistFiles.Count} tracks · {(station.Shuffle ? "Shuffle" : "Play in order")}"
            : station.Source;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ProviderNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string)?.ToLowerInvariant() switch
        {
            "mpv" => "Local / Radio Stream",
            "youtube" => "YouTube",
            "soundcloud" => "SoundCloud",
            "applemusic" => "Apple Music",
            "external-audio" => "External App",
            _ => "Music Source"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NonEmptyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
