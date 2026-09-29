using System.ComponentModel;
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
        public string Creator => string.IsNullOrWhiteSpace(Result.Creator) ? "Unknown creator" : Result.Creator;
        public string? ThumbnailUrl => Result.ThumbnailUrl;
        public string Detail => (Result.Type == MediaSearchResultType.Playlist ? "PLAYLIST · individual video durations vary" :
            "VIDEO" + (Result.Duration is { } duration ? " · " + (duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss")) : "")) +
            (AlreadyInLibrary ? " · ALREADY IN LIBRARY" : "");
    }

    readonly bool _previewAllowed;
    readonly MusicLibrary? _library;
    CancellationTokenSource? _searchCancellation;
    int _searchVersion;
    bool _closed;
    public MediaSearchResult? SelectedResult { get; private set; }

    public YouTubeSearchWindow(bool previewAllowed = true, MusicLibrary? library = null)
    {
        InitializeComponent();
        _previewAllowed = previewAllowed;
        _library = library;
        if (YouTubeSearchRuntime.KeySource == YouTubeSearchKeySource.None)
        {
            SearchButton.IsEnabled = false;
            StatusText.Text = "YouTube Search is not configured. You can still paste a link in the previous window.";
            ResultsEmptyText.Text = "Search is unavailable. A search key can be added under Settings → Music Services.";
        }
        if (!previewAllowed) PreviewHint.Text = "Preview is available when normal station playback is stopped.";
        Loaded += (_, _) => QueryBox.Focus();
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
        _searchVersion++;
        _searchCancellation?.Cancel();
        ResultsList.ItemsSource = null;
        SelectedResult = null;
        SelectButton.IsEnabled = false;
        SearchProgress.Visibility = Visibility.Collapsed;
        SearchButton.IsEnabled = YouTubeSearchRuntime.KeySource != YouTubeSearchKeySource.None;
        StatusText.Text = SearchButton.IsEnabled ? "Enter a query and press Enter or Search." :
            "YouTube Search is not configured. You can still paste a link in the previous window.";
        ResultsEmptyText.Text = SearchButton.IsEnabled ? "Search results will appear here." :
            "Search is unavailable. A search key can be added under Settings → Music Services.";
        ResultsEmptyText.Visibility = Visibility.Visible;
        StopPreview();
    }

    async Task SearchAsync()
    {
        if (_closed || YouTubeSearchRuntime.KeySource == YouTubeSearchKeySource.None) return;
        var version = ++_searchVersion;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        ResultsList.ItemsSource = null;
        SelectButton.IsEnabled = false;
        StopPreview();
        SearchButton.IsEnabled = false;
        SearchProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Searching YouTube…";
        ResultsEmptyText.Text = "Looking for music…";
        ResultsEmptyText.Visibility = Visibility.Visible;
        try
        {
            var type = TypeBox.SelectedIndex == 1 ? MediaSearchResultType.Playlist : MediaSearchResultType.Video;
            var results = await YouTubeSearchRuntime.SearchAsync(new MediaSearchQuery(QueryBox.Text, type), _searchCancellation.Token);
            if (_closed || version != _searchVersion) return;
            ResultsList.ItemsSource = results.Select(result => new ResultCard(result,
                _library is not null && MediaDiscoveryIngestion.IsAlreadyPresent(_library, result))).ToList();
            ResultsEmptyText.Text = "No results. Try another search or paste a link.";
            ResultsEmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = results.Count == 0 ? "No results found." :
                $"{results.Count} {type.ToString().ToLowerInvariant()} results · Select one to add it.";
            if (results.Count > 0) ResultsList.Focus();
        }
        catch (OperationCanceledException) { }
        catch (MediaDiscoveryException error)
        {
            if (_closed || version != _searchVersion) return;
            StatusText.Text = error.Message;
            ResultsEmptyText.Text = "Search unavailable right now. You can still paste a YouTube link.";
        }
        catch (Exception)
        {
            if (_closed || version != _searchVersion) return;
            StatusText.Text = "YouTube Search could not finish. Try again or paste a link.";
            ResultsEmptyText.Text = "Search unavailable right now. You can still paste a YouTube link.";
        }
        finally
        {
            if (!_closed && version == _searchVersion)
            {
                SearchButton.IsEnabled = true;
                SearchProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ResultsList.SelectedItem is null) return;
        e.Handled = true;
        Select_Click(sender, e);
    }

    void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not null) Select_Click(sender, e);
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
        _closed = true;
        _searchVersion++;
        _searchCancellation?.Cancel();
        StopPreview();
    }
}
