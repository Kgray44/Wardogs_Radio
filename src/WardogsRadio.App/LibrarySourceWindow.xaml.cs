using System.IO;
using System.Windows;
using Microsoft.Win32;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public sealed record LibrarySourceDraft(string ProviderId, string Source, string Name);

public partial class LibrarySourceWindow : Window
{
    readonly string _providerId;
    public LibrarySourceDraft? Result { get; private set; }

    public LibrarySourceWindow(string providerId)
    {
        InitializeComponent();
        _providerId = providerId;
        var youtube = providerId.Equals("youtube", StringComparison.OrdinalIgnoreCase);
        Heading.Text = youtube ? "ADD YOUTUBE VIDEO" : "ADD LOCAL MEDIA SOURCE";
        SourceLabel.Text = youtube ? "YOUTUBE VIDEO LINK" : "LOCAL MEDIA FILE";
        Explanation.Text = youtube
            ? "Use one YouTube video. Its song cues can be reused by several stations; playlists are not segmented as one source."
            : "Choose an existing media file. The library keeps its path and does not copy, move, or transcode the file.";
        BrowseButton.Visibility = youtube ? Visibility.Collapsed : Visibility.Visible;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose a media file to reference from the music library",
            Filter = "Audio and video|*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wav;*.wma;*.aiff;*.aif;*.alac;*.ape;*.wv;*.mka;*.mp4;*.mkv;*.webm|All files|*.*"
        };
        if (picker.ShowDialog(this) != true) return;
        SourceBox.Text = picker.FileName;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = Path.GetFileNameWithoutExtension(picker.FileName);
    }

    void SourceBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ValidationText.Text = "";

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(source)) { ValidationText.Text = "ENTER A MEDIA IDENTITY FIRST."; return; }
        if (_providerId == "youtube" && !MusicLibraryService.TryGetYouTubeVideoId(source, out _))
        {
            ValidationText.Text = "PASTE A SINGLE YOUTUBE VIDEO LINK, NOT A PLAYLIST.";
            return;
        }
        if (_providerId == "mpv" && !File.Exists(source))
        {
            ValidationText.Text = "CHOOSE AN EXISTING LOCAL MEDIA FILE.";
            return;
        }
        var normalized = MusicLibraryService.NormalizeSource(_providerId, source);
        Result = new LibrarySourceDraft(_providerId, normalized,
            string.IsNullOrWhiteSpace(NameBox.Text) ? Path.GetFileNameWithoutExtension(normalized) : NameBox.Text.Trim());
        DialogResult = true;
    }
}
