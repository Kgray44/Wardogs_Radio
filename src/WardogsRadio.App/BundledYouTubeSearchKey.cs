using System.IO;

namespace WardogsRadio.App;

internal static class BundledYouTubeSearchKey
{
    internal const string ResourceName = "WardogsRadio.YouTubeSearch.DefaultKey";

    internal static string? Read()
    {
        using var stream = typeof(BundledYouTubeSearchKey).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        var key = reader.ReadToEnd().Trim();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }
}
