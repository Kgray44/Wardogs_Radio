using System.Globalization;
using System.Windows;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class EditSongWindow : Window
{
    readonly double? _durationSeconds;
    public string SongTitle => SongName.Text.Trim();
    public double StartSeconds { get; private set; }
    public double? EndSeconds { get; private set; }

    public EditSongWindow(StationSong song, double? durationSeconds)
    {
        InitializeComponent();
        _durationSeconds = durationSeconds;
        SongName.Text = song.Name;
        StartTime.Text = FormatTime(song.StartSeconds);
        StopTime.Text = song.EndSeconds is { } end ? FormatTime(end) : "";
        SongName.Focus();
        SongName.SelectAll();
    }

    static string FormatTime(double seconds)
    {
        var whole = (long)Math.Floor(seconds);
        var fraction = seconds - whole;
        var time = TimeSpan.FromSeconds(whole);
        var result = time.TotalHours >= 1 ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(long)time.TotalMinutes:00}:{time.Seconds:00}";
        return fraction < .0005 ? result : result + fraction.ToString("0.###", CultureInfo.InvariantCulture)[1..];
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SongPlaylist.TryParseTime(StartTime.Text, out var start))
        {
            ErrorText.Text = "Enter a valid start time (MM:SS, H:MM:SS, or seconds).";
            StartTime.Focus();
            return;
        }
        double? end = null;
        if (!string.IsNullOrWhiteSpace(StopTime.Text))
        {
            if (!SongPlaylist.TryParseTime(StopTime.Text, out var parsed))
            {
                ErrorText.Text = "Enter a valid stop time, or leave it blank for the end.";
                StopTime.Focus();
                return;
            }
            end = parsed;
        }
        if (string.IsNullOrWhiteSpace(SongTitle))
        {
            ErrorText.Text = "Enter a song name.";
            SongName.Focus();
            return;
        }
        if (end is { } stop && stop <= start + .25 || _durationSeconds is { } duration &&
            (start >= duration - .25 || end is { } last && last > duration + .01))
        {
            ErrorText.Text = "The stop must be after the start and both times must fit inside the original track.";
            return;
        }
        StartSeconds = start;
        EndSeconds = end;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
