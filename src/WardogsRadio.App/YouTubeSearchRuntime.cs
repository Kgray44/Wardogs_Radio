using System.Net.Http;
using WardogsRadio.Core;

namespace WardogsRadio.App;

internal enum YouTubeSearchStatus { Ready, Quota, Error }

/// <summary>One app-session search state shared by Settings, diagnostics, and the native search window.</summary>
internal static class YouTubeSearchRuntime
{
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    static YouTubeSearchKeys? _keys;
    static YouTubeDiscoveryProvider? _provider;
    static MediaDiscoveryFailure? _lastFailure;

    public static DateTimeOffset? LastSearchUtc { get; private set; }
    public static string? LastError { get; private set; }
    public static YouTubeSearchKeySource KeySource => _keys?.Source ?? YouTubeSearchKeySource.BuiltInDefault;
    public static bool HasUserOverride => _keys?.HasUserOverride == true;
    public static YouTubeSearchStatus Status => _lastFailure == MediaDiscoveryFailure.Quota ? YouTubeSearchStatus.Quota :
        _lastFailure is null ? YouTubeSearchStatus.Ready : YouTubeSearchStatus.Error;

    public static void Initialize(string configurationRoot)
    {
        var overrideStore = new WindowsYouTubeSearchOverrideStore(configurationRoot);
        _keys = new YouTubeSearchKeys(overrideStore);
        _provider = new YouTubeDiscoveryProvider(Client, () => _keys.CurrentKey);
        if (overrideStore.ReadFailed)
        {
            _lastFailure = MediaDiscoveryFailure.Authentication;
            LastError = "The saved custom key could not be opened. Restore the default key or save a new custom key.";
        }
    }

    public static async Task<IReadOnlyList<MediaSearchResult>> SearchAsync(MediaSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await Provider.SearchAsync(query, cancellationToken);
            LastSearchUtc = DateTimeOffset.UtcNow;
            _lastFailure = null;
            LastError = null;
            return results;
        }
        catch (MediaDiscoveryException error)
        {
            if (error.Failure != MediaDiscoveryFailure.InvalidQuery)
            {
                _lastFailure = error.Failure;
                LastError = error.Message;
            }
            throw;
        }
    }

    public static async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Provider.TestConnectionAsync(cancellationToken);
            _lastFailure = null;
            LastError = null;
        }
        catch (MediaDiscoveryException error)
        {
            _lastFailure = error.Failure;
            LastError = error.Message;
            throw;
        }
    }

    public static void UseCustomKey(string key)
    {
        Keys.UseCustomKey(key);
        ResetHealth();
    }

    public static void RestoreDefault()
    {
        Keys.RestoreDefault();
        ResetHealth();
    }

    static void ResetHealth()
    {
        Provider.ClearCache();
        _lastFailure = null;
        LastError = null;
    }

    static YouTubeSearchKeys Keys => _keys ?? throw new InvalidOperationException("YouTube Search was not initialized.");
    static YouTubeDiscoveryProvider Provider => _provider ?? throw new InvalidOperationException("YouTube Search was not initialized.");
}
