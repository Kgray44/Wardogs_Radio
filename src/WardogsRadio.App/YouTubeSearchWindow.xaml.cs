using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class YouTubeSearchWindow : Window
{
    sealed record ResultCard(MediaSearchResult Result, bool AlreadyInLibrary)
    {
        public string Title => Result.Title;
        public string? Creator => Result.Creator;
        public string? ThumbnailUrl => Result.ThumbnailUrl;
        public string Detail => (Result.Type == MediaSearchResultType.Playlist ? "PLAYLIST · individual video durations vary" :
            "VIDEO" + (Result.Duration is { } duration ? " · " + (duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss")) : "")) +
            (AlreadyInLibrary ? " · ALREADY IN LIBRARY" : "");
    }

    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    static string? _sessionKey;
    public static bool IsConfiguredForSession => !string.IsNullOrWhiteSpace(_sessionKey);
    public static DateTimeOffset? LastSearchUtc { get; private set; }
    public static string? LastSearchError { get; private set; }
    static readonly YouTubeDiscoveryProvider Provider = new(Client, () => _sessionKey);
    readonly bool _previewAllowed;
    readonly MusicLibrary? _library;
    CancellationTokenSource? _searchCancellation;
    public MediaSearchResult? SelectedResult { get; private set; }

    public YouTubeSearchWindow(bool previewAllowed = true, MusicLibrary? library = null)
    {
        InitializeComponent();
        _previewAllowed = previewAllowed;
        _library = library;
        KeyPanel.Visibility = string.IsNullOrWhiteSpace(_sessionKey) ? Visibility.Visible : Visibility.Collapsed;
        ChangeKeyButton.Visibility = string.IsNullOrWhiteSpace(_sessionKey) ? Visibility.Collapsed : Visibility.Visible;
        if (!previewAllowed) PreviewHint.Text = "Preview is available when normal station playback is stopped.";
        Loaded += (_, _) => QueryBox.Focus();
    }

    void UseKey_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(KeyBox.Password)) { StatusText.Text = "Enter a YouTube Data API key, or paste a link instead."; return; }
        _sessionKey = KeyBox.Password.Trim();
        KeyBox.Clear();
        KeyPanel.Visibility = Visibility.Collapsed;
        ChangeKeyButton.Visibility = Visibility.Visible;
        StatusText.Text = "Search is configured for this app session. The key is kept in memory only.";
    }

    void ChangeKey_Click(object sender, RoutedEventArgs e)
    {
        KeyPanel.Visibility = Visibility.Visible;
        ChangeKeyButton.Visibility = Visibility.Collapsed;
        KeyBox.Focus();
    }

    void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = SearchAsync();
    }

    void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList is null) return;
        ResultsList.ItemsSource = null;
        SelectedResult = null;
        SelectButton.IsEnabled = false;
        StopPreview();
    }

    async Task SearchAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        ResultsList.ItemsSource = null;
        SelectButton.IsEnabled = false;
        StopPreview();
        StatusText.Text = "Searching YouTube…";
        try
        {
            var type = TypeBox.SelectedIndex == 1 ? MediaSearchResultType.Playlist : MediaSearchResultType.Video;
            var results = await Provider.SearchAsync(new MediaSearchQuery(QueryBox.Text, type), _searchCancellation.Token);
            LastSearchUtc = DateTimeOffset.UtcNow;
            LastSearchError = null;
            ResultsList.ItemsSource = results.Select(result => new ResultCard(result,
                _library is not null && MediaDiscoveryIngestion.IsAlreadyPresent(_library, result))).ToList();
            StatusText.Text = results.Count == 0 ? "No results. Try another query or paste a link." : $"{results.Count} {type.ToString().ToLowerInvariant()} results. Select one to add it.";
        }
        catch (OperationCanceledException) { }
        catch (MediaDiscoveryException error)
        {
            StatusText.Text = error.Message;
            LastSearchError = error.Message;
            if (error.Message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                error.Message.Contains("configuration", StringComparison.OrdinalIgnoreCase))
            {
                KeyPanel.Visibility = Visibility.Visible;
                ChangeKeyButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception)
        {
            StatusText.Text = "YouTube Search could not finish. Try again or paste a link.";
            LastSearchError = "Unexpected search failure";
        }
    }

    void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var card = ResultsList.SelectedItem as ResultCard;
        SelectButton.IsEnabled = card is not null;
        PreviewButton.IsEnabled = _previewAllowed && card?.Result.Type == MediaSearchResultType.Video;
        SelectedText.Text = card is null ? "No selection" : card.Result.Type + " · " + card.Title +
            (card.AlreadyInLibrary ? " · Already in Library; selection reuses it" : "");
        SelectButton.Content = card?.AlreadyInLibrary == true ? "USE EXISTING" : "SELECT";
        StopPreview();
    }

    async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewAllowed || ResultsList.SelectedItem is not ResultCard { Result.Type: MediaSearchResultType.Video } card) return;
        try
        {
            await PreviewView.EnsureCoreWebView2Async();
            PreviewView.Visibility = Visibility.Visible;
            PreviewView.CoreWebView2.Navigate("https://www.youtube-nocookie.com/embed/" + Uri.EscapeDataString(card.Result.MediaId) + "?autoplay=1");
            StopPreviewButton.IsEnabled = true;
            PreviewHint.Text = "Preview only. This does not add anything to your Library or Listening History.";
        }
        catch (Exception)
        {
            StopPreview();
            PreviewHint.Text = "Preview is unavailable on this device. You can still select the result.";
        }
    }

    void StopPreview_Click(object sender, RoutedEventArgs e) => StopPreview();

    void StopPreview()
    {
        if (PreviewView?.CoreWebView2 is not null) PreviewView.CoreWebView2.Navigate("about:blank");
        if (PreviewView is not null) PreviewView.Visibility = Visibility.Collapsed;
        if (StopPreviewButton is not null) StopPreviewButton.IsEnabled = false;
    }

    void Select_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultCard card) return;
        SelectedResult = card.Result;
        StopPreview();
        DialogResult = true;
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        _searchCancellation?.Cancel();
        StopPreview();
    }
}
