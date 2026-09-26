using System.IO;
using System.Windows;
using Microsoft.Win32;
using WardogsRadio.Core;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

public sealed record LibrarySourceDraft(string ProviderId, string Source, string Name);

public partial class LibrarySourceWindow : Window
{
    readonly string _selection;
    readonly string _providerId;
    public LibrarySourceDraft? Result { get; private set; }

    public LibrarySourceWindow(string selection)
    {
        InitializeComponent();
        _selection = selection;
        _providerId = StationSourceSelection.ProviderId(selection);
        var local = selection == StationSourceSelection.LocalMpv;
        (Heading.Text, SourceLabel.Text, Explanation.Text) = selection switch
        {
            StationSourceSelection.LocalMpv => ("ADD LOCAL MEDIA SOURCE", "LOCAL MEDIA FILE", "Choose an existing media file. The library keeps its path and does not copy, move, or transcode the file."),
            StationSourceSelection.LinkMpv => ("ADD INTERNET RADIO SOURCE", "STREAM LINK", "Save the direct stream identity used by your station. Streams can be shared, but do not expose reusable timeline cues."),
            "youtube" => ("ADD YOUTUBE VIDEO", "YOUTUBE VIDEO LINK", "Use one YouTube video. Its song cues can be reused by several stations; playlists are not segmented as one source."),
            "external-audio" => ("ADD MUSIC APP SOURCE", "WINDOWS MEDIA SESSION", "Enter the media-session identity selected on the Stations page. It can be shared as a source record, but does not expose timeline cues."),
            "soundcloud" => ("ADD SOUNDCLOUD SOURCE", "SOUNDCLOUD LINK", "Save a SoundCloud source identity for compatible stations. Playback still needs the account access configured for this app."),
            "applemusic" => ("ADD APPLE MUSIC SOURCE", "APPLE MUSIC LINK", "Save an Apple Music source identity for compatible stations. Playback still needs the account access configured for this app."),
            _ => ("ADD SOURCE", "SOURCE IDENTITY", "Save the source identity used by compatible stations.")
        };
        BrowseButton.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
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
        if (_selection == "youtube" && !MusicLibraryService.TryGetYouTubeVideoId(source, out _))
            { ValidationText.Text = "PASTE A SINGLE YOUTUBE VIDEO LINK, NOT A PLAYLIST."; return; }
        if (_selection == StationSourceSelection.LocalMpv && !File.Exists(source))
            { ValidationText.Text = "CHOOSE AN EXISTING LOCAL MEDIA FILE."; return; }
        if (_selection == StationSourceSelection.LinkMpv && !IsWebLink(source))
            { ValidationText.Text = "ENTER A FULL HTTP:// OR HTTPS:// STREAM LINK."; return; }
        if (_selection == "soundcloud")
        {
            var validation = SoundCloudUrl.Normalize(source);
            if (!validation.IsValid) { ValidationText.Text = validation.Message; return; }
            source = validation.CanonicalSource ?? source;
        }
        if (_selection == "applemusic")
        {
            var validation = AppleMusicUrl.Normalize(source);
            if (!validation.IsValid) { ValidationText.Text = validation.Message; return; }
            source = validation.CanonicalSource ?? source;
        }
        var normalized = MusicLibraryService.NormalizeSource(_providerId, source);
        Result = new LibrarySourceDraft(_providerId, normalized,
            string.IsNullOrWhiteSpace(NameBox.Text) ? DefaultName(normalized) : NameBox.Text.Trim());
        DialogResult = true;
    }

    static bool IsWebLink(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    static string DefaultName(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)) return uri.Host + uri.AbsolutePath.TrimEnd('/');
        return Path.GetFileNameWithoutExtension(source);
    }
}
