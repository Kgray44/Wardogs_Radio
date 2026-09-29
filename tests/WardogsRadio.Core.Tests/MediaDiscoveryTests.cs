using System.Net;
using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class MediaDiscoveryTests
{
    [Fact]
    public void SearchSelectionAndPastedUrlReuseTheSameSourceAndStationSong()
    {
        var library = new MusicLibrary();
        var pasted = MusicLibraryService.EnsureSource(library, "youtube", "https://music.youtube.com/watch?v=abcDEF12345&list=PLignored", "Video");
        var station = new Station { ProviderId = "youtube", Name = "Combat" };
        var result = Video();

        var first = MediaDiscoveryIngestion.Add(library, result, station);
        var second = MediaDiscoveryIngestion.Add(library, result, station);

        Assert.Equal(pasted.Id, first.Source.Id);
        Assert.True(first.AlreadyPresent);
        Assert.Equal(first.Song!.Id, second.Song!.Id);
        Assert.Single(library.Sources);
        Assert.Single(library.Songs);
        Assert.Single(station.PlaylistEntries);
        Assert.Equal(result.CanonicalUrl, WardogsRadio.Playback.YouTubeUrl.Normalize(
            "https://music.youtube.com/watch?v=abcDEF12345&list=PLignored").CanonicalSource);
    }

    [Fact]
    public void PlaylistSelectionStaysACollectionWithoutInventingOneTimelineSong()
    {
        var library = new MusicLibrary();
        var result = new MediaSearchResult("youtube", "PL1234567890", MediaSearchResultType.Playlist,
            "https://www.youtube.com/playlist?list=PL1234567890", "Vietnam mix", "Creator", null, null, null);

        var first = MediaDiscoveryIngestion.Add(library, result);
        var second = MusicLibraryService.EnsureSource(library, "youtube", "https://music.youtube.com/playlist?list=PL1234567890&si=tracking");

        Assert.Equal(first.Source.Id, second.Id);
        Assert.Null(first.Song);
        Assert.Empty(library.Songs);
        Assert.False(MusicLibraryService.SupportsCueRanges("youtube", first.Source.Source));
    }

    [Fact]
    public void AbandonedSearchHasNoLibrarySideEffects()
    {
        var library = new MusicLibrary();
        _ = Video();
        Assert.Empty(library.Sources);
        Assert.Empty(library.Songs);
    }

    [Fact]
    public async Task SearchParsesVideoMetadataAndCachesAnExplicitQuery()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            calls++;
            Assert.DoesNotContain("key=", request.RequestUri!.Query, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            if (request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
                Assert.Contains("videoEmbeddable=true", request.RequestUri.Query);
            var body = request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? """{"items":[{"id":{"videoId":"abcDEF12345"},"snippet":{"title":"Fortunate Son","channelTitle":"CCR","description":"Official","thumbnails":{"medium":{"url":"https://example.com/thumb.jpg"}}}}]}"""
                : """{"items":[{"id":"abcDEF12345","contentDetails":{"duration":"PT4M22S"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var provider = new YouTubeDiscoveryProvider(client, () => "test-key");

        var first = await provider.SearchAsync(new MediaSearchQuery("Fortunate Son", MediaSearchResultType.Video));
        var second = await provider.SearchAsync(new MediaSearchQuery("fortunate son", MediaSearchResultType.Video));

        Assert.Same(first, second);
        Assert.Equal(2, calls); // One explicit search and one duration lookup.
        Assert.Equal(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(22), Assert.Single(first).Duration);
    }

    [Fact]
    public async Task MissingKeyLeavesSearchUnavailableWithoutSendingARequest()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); }));
        var noKey = new YouTubeDiscoveryProvider(client, () => null);

        var error = await Assert.ThrowsAsync<MediaDiscoveryException>(() =>
            noKey.SearchAsync(new MediaSearchQuery("music", MediaSearchResultType.Video)));
        Assert.Equal(MediaDiscoveryFailure.NotConfigured, error.Failure);
        Assert.Contains("paste", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "keyInvalid", MediaDiscoveryFailure.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, "quotaExceeded", MediaDiscoveryFailure.Quota)]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded", MediaDiscoveryFailure.Quota)]
    public async Task ApiFailuresAreClassifiedWithoutExposingTheKey(HttpStatusCode status, string reason,
        MediaDiscoveryFailure expected)
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"error\":{\"errors\":[{\"reason\":\"" + reason + "\"}]}}")
        }));
        var provider = new YouTubeDiscoveryProvider(client, () => "private-test-key");

        var error = await Assert.ThrowsAsync<MediaDiscoveryException>(() =>
            provider.SearchAsync(new MediaSearchQuery("music", MediaSearchResultType.Video)));
        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain("private-test-key", error.Message);
        Assert.Contains("paste", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectionTestUsesFreshExplicitSearchAndDoesNotUseCachedResults()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            calls++;
            Assert.Contains("/search", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[]}") };
        }));
        var provider = new YouTubeDiscoveryProvider(client, () => "test-key");

        await provider.SearchAsync(new MediaSearchQuery("music", MediaSearchResultType.Playlist));
        await provider.SearchAsync(new MediaSearchQuery("music", MediaSearchResultType.Playlist));
        await provider.TestConnectionAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NetworkFailureUsesAReadableFallbackMessage()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("private-test-key")));
        var provider = new YouTubeDiscoveryProvider(client, () => "private-test-key");
        var error = await Assert.ThrowsAsync<MediaDiscoveryException>(() =>
            provider.SearchAsync(new MediaSearchQuery("music", MediaSearchResultType.Video)));
        Assert.Equal(MediaDiscoveryFailure.Network, error.Failure);
        Assert.DoesNotContain("private-test-key", error.Message);
    }

    static MediaSearchResult Video() => new("youtube", "abcDEF12345", MediaSearchResultType.Video,
        "https://www.youtube.com/watch?v=abcDEF12345", "Fortunate Son", "CCR", null, TimeSpan.FromMinutes(4), null);

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
