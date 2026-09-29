using System.Text;
using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class YouTubeSearchKeysTests
{
    const string DefaultKey = "default-api-key-123456789012345";
    const string OverrideKey = "personal-api-key-12345678901234";

    [Fact]
    public void DefaultOverrideAndRestoreFollowResolutionOrder()
    {
        var store = new MemoryStore();
        var keys = new YouTubeSearchKeys(DefaultKey, store);
        Assert.Equal(YouTubeSearchKeySource.BundledDefault, keys.Source);
        Assert.Equal(DefaultKey, keys.CurrentKey);

        keys.UseCustomKey(OverrideKey);
        Assert.Equal(YouTubeSearchKeySource.UserOverride, keys.Source);
        Assert.Equal(OverrideKey, keys.CurrentKey);
        Assert.Equal(OverrideKey, new YouTubeSearchKeys(DefaultKey, store).CurrentKey);

        keys.RestoreDefault();
        Assert.Equal(YouTubeSearchKeySource.BundledDefault, keys.Source);
        Assert.Equal(DefaultKey, keys.CurrentKey);
        Assert.Null(store.Read());
    }

    [Fact]
    public void MissingDefaultAndOverrideMeanSearchIsUnconfigured()
    {
        var keys = new YouTubeSearchKeys(null, new MemoryStore());
        Assert.Equal(YouTubeSearchKeySource.None, keys.Source);
        Assert.Null(keys.CurrentKey);
        keys.UseCustomKey(OverrideKey);
        Assert.Equal(OverrideKey, keys.CurrentKey);
        keys.RestoreDefault();
        Assert.Equal(YouTubeSearchKeySource.None, keys.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("invalid key with spaces 1234567890")]
    public void InvalidOverrideIsNotPersisted(string value)
    {
        var store = new MemoryStore();
        var keys = new YouTubeSearchKeys(DefaultKey, store);
        Assert.Throws<ArgumentException>(() => keys.UseCustomKey(value));
        Assert.Null(store.Read());
        Assert.Equal(DefaultKey, keys.CurrentKey);
    }

    [Fact]
    public void WindowsOverrideIsProtectedAndSeparateFromConfiguration()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "wardogs-search-key-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WindowsYouTubeSearchOverrideStore(root);
            store.Write(OverrideKey);
            Assert.Equal(OverrideKey, store.Read());
            Assert.DoesNotContain(OverrideKey, Encoding.UTF8.GetString(File.ReadAllBytes(store.PathName)));
            Assert.False(File.Exists(Path.Combine(root, "config.json")));
            store.Clear();
            Assert.Null(store.Read());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    sealed class MemoryStore : IYouTubeSearchOverrideStore
    {
        string? _key;
        public string? Read() => _key;
        public void Write(string key) => _key = key;
        public void Clear() => _key = null;
    }
}
