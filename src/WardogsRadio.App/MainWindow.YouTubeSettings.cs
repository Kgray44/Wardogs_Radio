using System.IO;
using System.Windows;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class MainWindow
{
    bool _updatingYouTubeSearchSettings;

    void RefreshYouTubeSearchSettings()
    {
        if (YouTubeSearchStatusText is null) return;
        YouTubeSearchStatusText.Text = YouTubeSearchRuntime.Status switch
        {
            YouTubeSearchStatus.Ready => "READY",
            YouTubeSearchStatus.NotConfigured => "NOT CONFIGURED",
            YouTubeSearchStatus.Quota => "QUOTA",
            _ => "ERROR"
        };
        var source = YouTubeSearchRuntime.KeySource switch
        {
            YouTubeSearchKeySource.UserOverride => "Using your custom key.",
            YouTubeSearchKeySource.BundledDefault => "Using the WARDOGS default search key.",
            _ => "No search key is installed. Paste YouTube links until a packaged default or custom key is available."
        };
        YouTubeSearchDetailText.Text = YouTubeSearchRuntime.LastError is { } error ? source + " " + error : source;
        _updatingYouTubeSearchSettings = true;
        UseCustomApiKeyCheck.IsChecked = YouTubeSearchRuntime.HasUserOverride;
        YouTubeCustomKeyPanel.Visibility = YouTubeSearchRuntime.HasUserOverride ? Visibility.Visible : Visibility.Collapsed;
        _updatingYouTubeSearchSettings = false;
    }

    void UseCustomApiKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingYouTubeSearchSettings || YouTubeCustomKeyPanel is null) return;
        if (UseCustomApiKeyCheck.IsChecked == true)
        {
            YouTubeCustomKeyPanel.Visibility = Visibility.Visible;
            YouTubeCustomKeyBox.Focus();
            return;
        }
        if (YouTubeSearchRuntime.HasUserOverride) RestoreYouTubeDefaultKey();
        else YouTubeCustomKeyPanel.Visibility = Visibility.Collapsed;
    }

    void YouTubeSaveCustomKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            YouTubeSearchRuntime.UseCustomKey(YouTubeCustomKeyBox.Password);
            YouTubeCustomKeyBox.Clear();
            RefreshYouTubeSearchSettings();
            Footer.Text = "CUSTOM YOUTUBE SEARCH KEY SAVED FOR THIS WINDOWS USER.";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            YouTubeCustomKeyBox.Clear();
            YouTubeSearchDetailText.Text = "The custom key could not be saved. Check the key and local storage permissions.";
        }
    }

    void YouTubeRestoreDefaultKey_Click(object sender, RoutedEventArgs e) => RestoreYouTubeDefaultKey();

    void RestoreYouTubeDefaultKey()
    {
        try
        {
            YouTubeSearchRuntime.RestoreDefault();
            YouTubeCustomKeyBox.Clear();
            RefreshYouTubeSearchSettings();
            Footer.Text = YouTubeSearchRuntime.KeySource == YouTubeSearchKeySource.BundledDefault
                ? "WARDOGS DEFAULT YOUTUBE SEARCH KEY RESTORED."
                : "CUSTOM KEY REMOVED · YouTube links still work; Search needs a packaged default key.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            YouTubeSearchDetailText.Text = "The custom key could not be removed. Check local storage permissions.";
            _updatingYouTubeSearchSettings = true;
            UseCustomApiKeyCheck.IsChecked = YouTubeSearchRuntime.HasUserOverride;
            _updatingYouTubeSearchSettings = false;
        }
    }

    async void YouTubeTestConnection_Click(object sender, RoutedEventArgs e)
    {
        YouTubeTestConnectionButton.IsEnabled = false;
        YouTubeSearchStatusText.Text = "TESTING…";
        YouTubeSearchDetailText.Text = "Checking YouTube Search. This uses one search request.";
        try
        {
            await YouTubeSearchRuntime.TestConnectionAsync();
            RefreshYouTubeSearchSettings();
            YouTubeSearchDetailText.Text += " Connection passed.";
        }
        catch (MediaDiscoveryException) { RefreshYouTubeSearchSettings(); }
        catch (Exception)
        {
            YouTubeSearchStatusText.Text = "ERROR";
            YouTubeSearchDetailText.Text = "Connection test could not finish. Try again later or paste a YouTube link.";
        }
        finally { YouTubeTestConnectionButton.IsEnabled = true; }
    }
}
