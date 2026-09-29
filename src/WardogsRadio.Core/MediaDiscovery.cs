using System.Net;
using System.Text.Json;
using System.Xml;

namespace WardogsRadio.Core;

public enum MediaSearchResultType { Video, Playlist }

public sealed record MediaSearchQuery(string Text, MediaSearchResultType Type);

public sealed record MediaSearchResult(string ProviderId, string MediaId, MediaSearchResultType Type,
    string CanonicalUrl, string Title, string? Creator, string? ThumbnailUrl, TimeSpan? Duration,
    string? Description);

public interface IMediaDiscoveryProvider
{
    Task<IReadOnlyList<MediaSearchResult>> SearchAsync(MediaSearchQuery query, CancellationToken cancellationToken = default);
}

public enum MediaDiscoveryFailure { NotConfigured, InvalidQuery, Authentication, Quota, Network, Service, InvalidResponse }

public sealed class MediaDiscoveryException(MediaDiscoveryFailure failure, string userMessage) : Exception(userMessage)
{
    public MediaDiscoveryFailure Failure { get; } = failure;
}

/// <summary>Explicit, session-cached YouTube Data API searches. Credentials are supplied by the caller and never logged.</summary>
public sealed class YouTubeDiscoveryProvider(HttpClient client, Func<string?> apiKey) : IMediaDiscoveryProvider
{
    readonly Dictionary<MediaSearchQuery, IReadOnlyList<MediaSearchResult>> _cache = new();

    public async Task<IReadOnlyList<MediaSearchResult>> SearchAsync(MediaSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var text = query.Text.Trim();
        if (text.Length is < 2 or > 150) throw new MediaDiscoveryException(MediaDiscoveryFailure.InvalidQuery,
            "Enter 2 to 150 characters to search YouTube.");
        var normalized = new MediaSearchQuery(text.ToUpperInvariant(), query.Type);
        if (_cache.TryGetValue(normalized, out var cached)) return cached;
        var key = apiKey()?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new MediaDiscoveryException(MediaDiscoveryFailure.NotConfigured,
                "YouTube Search is not configured. You can still paste a YouTube link.");
        try
        {
            var kind = query.Type == MediaSearchResultType.Video ? "video" : "playlist";
            var url = "https://www.googleapis.com/youtube/v3/search?part=snippet&type=" + kind +
                (query.Type == MediaSearchResultType.Video ? "&videoEmbeddable=true" : "") +
                "&maxResults=25&q=" + Uri.EscapeDataString(text);
            using var response = await SendWithKeyAsync(url, key, cancellationToken);
            if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new MediaDiscoveryException(MediaDiscoveryFailure.InvalidResponse,
                    "YouTube returned an unexpected search response. Try again later.");
            var results = new List<MediaSearchResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("snippet", out var snippet) || snippet.ValueKind != JsonValueKind.Object) continue;
                var idName = query.Type == MediaSearchResultType.Video ? "videoId" : "playlistId";
                if (!id.TryGetProperty(idName, out var identity)) continue;
                var mediaId = identity.GetString();
                if (string.IsNullOrWhiteSpace(mediaId) || !seen.Add(mediaId)) continue;
                var canonical = query.Type == MediaSearchResultType.Video
                    ? "https://www.youtube.com/watch?v=" + mediaId
                    : "https://www.youtube.com/playlist?list=" + mediaId;
                var title = GetString(snippet, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;
                var thumbnail = snippet.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.ValueKind == JsonValueKind.Object &&
                    thumbnails.TryGetProperty("medium", out var medium) ? GetString(medium, "url") : null;
                results.Add(new MediaSearchResult("youtube", mediaId, query.Type, canonical, title,
                    GetString(snippet, "channelTitle"), thumbnail, null, GetString(snippet, "description")));
            }
            if (query.Type == MediaSearchResultType.Video && results.Count > 0)
                results = await WithDurationsAsync(results, key, cancellationToken);
            if (_cache.Count >= 50) _cache.Clear();
            _cache[normalized] = results;
            return results;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaDiscoveryException(MediaDiscoveryFailure.Network, "YouTube Search timed out. Try again or paste a link."); }
        catch (HttpRequestException)
        { throw new MediaDiscoveryException(MediaDiscoveryFailure.Network, "WARDOGS couldn't reach YouTube Search. You can still paste a link."); }
        catch (JsonException)
        { throw new MediaDiscoveryException(MediaDiscoveryFailure.InvalidResponse, "YouTube returned an unreadable search response. Try again later."); }
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var key = apiKey()?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new MediaDiscoveryException(MediaDiscoveryFailure.NotConfigured,
                "YouTube Search is not configured. You can still paste a YouTube link.");
        try
        {
            using var response = await SendWithKeyAsync(
                "https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&videoEmbeddable=true&maxResults=1&q=music",
                key, cancellationToken);
            if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new MediaDiscoveryException(MediaDiscoveryFailure.InvalidResponse,
                    "YouTube returned an unexpected search response. Try again later.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaDiscoveryException(MediaDiscoveryFailure.Network, "YouTube Search timed out. Try again later."); }
        catch (HttpRequestException)
        { throw new MediaDiscoveryException(MediaDiscoveryFailure.Network, "WARDOGS couldn't reach YouTube Search."); }
        catch (JsonException)
        { throw new MediaDiscoveryException(MediaDiscoveryFailure.InvalidResponse, "YouTube returned an unreadable search response."); }
    }

    public void ClearCache() => _cache.Clear();

    async Task<HttpResponseMessage> SendWithKeyAsync(string url, string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        return await client.SendAsync(request, cancellationToken);
    }

    static async Task<MediaDiscoveryException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? reason = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                reason = errors.EnumerateArray().Select(item => GetString(item, "reason")).FirstOrDefault(value => value is not null);
        }
        catch (JsonException) { /* Status code still permits a safe, generic error. */ }
        var quota = reason is "quotaExceeded" or "dailyLimitExceeded" or "userRateLimitExceeded" or "rateLimitExceeded" ||
            response.StatusCode == HttpStatusCode.TooManyRequests;
        if (quota) return new(MediaDiscoveryFailure.Quota,
            "YouTube Search has reached its quota or rate limit. Try again later, use a custom key in Settings, or paste a link.");
        if (reason is "keyInvalid" or "accessNotConfigured" or "ipRefererBlocked" or "forbidden" ||
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new(MediaDiscoveryFailure.Authentication,
                "YouTube Search could not authenticate. Check the key under Settings → Music Services, or paste a link.");
        return new(MediaDiscoveryFailure.Service,
            "YouTube Search is temporarily unavailable. Try again later or paste a link.");
    }

    async Task<List<MediaSearchResult>> WithDurationsAsync(List<MediaSearchResult> results, string key,
        CancellationToken cancellationToken)
    {
        var url = "https://www.googleapis.com/youtube/v3/videos?part=contentDetails&id=" +
            Uri.EscapeDataString(string.Join(',', results.Select(result => result.MediaId)));
        using var response = await SendWithKeyAsync(url, key, cancellationToken);
        if (!response.IsSuccessStatusCode) return results;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return results;
        var durations = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var id = GetString(item, "id");
            if (id is null || item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("contentDetails", out var details)) continue;
            var raw = GetString(details, "duration");
            try { if (raw is not null) durations[id] = XmlConvert.ToTimeSpan(raw); }
            catch (FormatException) { /* Duration stays unknown. */ }
        }
        return results.Select(result => durations.TryGetValue(result.MediaId, out var duration)
            ? result with { Duration = duration } : result).ToList();
    }

    static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>All discovery selections enter the same canonical source and song graph as pasted links.</summary>
public static class MediaDiscoveryIngestion
{
    public static bool IsAlreadyPresent(MusicLibrary library, MediaSearchResult result)
    {
        var normalized = MusicLibraryService.NormalizeSource(result.ProviderId, result.CanonicalUrl);
        return library.Sources.Any(source => source.ProviderId == result.ProviderId &&
            MusicLibraryService.NormalizeSource(source.ProviderId, source.Source).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static (MediaSource Source, LibrarySong? Song, bool AlreadyPresent) Add(MusicLibrary library,
        MediaSearchResult result, Station? station = null)
    {
        var normalized = MusicLibraryService.NormalizeSource(result.ProviderId, result.CanonicalUrl);
        var existing = library.Sources.FirstOrDefault(source => source.ProviderId == result.ProviderId &&
            MusicLibraryService.NormalizeSource(source.ProviderId, source.Source).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        var source = MusicLibraryService.EnsureSource(library, result.ProviderId, normalized, result.Title,
            result.Duration?.TotalSeconds);
        LibrarySong? song = null;
        if (result.Type == MediaSearchResultType.Video)
        {
            song = library.Songs.FirstOrDefault(candidate => candidate.SourceId == source.Id && candidate.StartSeconds == 0 && candidate.EndSeconds is null)
                ?? MusicLibraryService.EnsureWholeSourceSong(library, source, result.Title);
            if (station is not null && !station.PlaylistEntries.Any(entry => entry.SongId == song.Id))
                MusicLibraryService.AddSongToStation(station, song.Id);
        }
        return (source, song, existing is not null);
    }
}
