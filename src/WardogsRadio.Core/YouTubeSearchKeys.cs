using System.Security.Cryptography;
using System.Text;

namespace WardogsRadio.Core;

public enum YouTubeSearchKeySource { BuiltInDefault, UserOverride }

public interface IYouTubeSearchOverrideStore
{
    string? Read();
    void Write(string key);
    void Clear();
}

/// <summary>Resolves the local override before the built-in WARDOGS key.</summary>
public sealed class YouTubeSearchKeys
{
    // Replace this one value to change the shared key in future WARDOGS builds.
    public const string BuiltInDefaultKey = "AIzaSyAw_Ke_DYIAL3hQ6MnptnVBJJsIgNoz3UQ";

    readonly IYouTubeSearchOverrideStore _store;
    string? _override;

    public YouTubeSearchKeys(IYouTubeSearchOverrideStore store)
    {
        _store = store;
        _override = Normalize(store.Read());
    }

    public bool HasUserOverride => _override is not null;
    public YouTubeSearchKeySource Source => _override is not null ? YouTubeSearchKeySource.UserOverride : YouTubeSearchKeySource.BuiltInDefault;
    public string CurrentKey => _override ?? BuiltInDefaultKey;

    public void UseCustomKey(string key)
    {
        var normalized = Normalize(key) ?? throw new ArgumentException("Enter a YouTube Data API key.", nameof(key));
        if (normalized.Length is < 20 or > 256 || normalized.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter a valid single-line YouTube Data API key.", nameof(key));
        _store.Write(normalized);
        _override = normalized;
    }

    public void RestoreDefault()
    {
        _store.Clear();
        _override = null;
    }

    static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

/// <summary>Only the current Windows user can decrypt the optional local override.</summary>
public sealed class WindowsYouTubeSearchOverrideStore(string applicationRoot) : IYouTubeSearchOverrideStore
{
    public string PathName => Path.Combine(applicationRoot, "Search", "youtube-key-v1.dat");
    public bool ReadFailed { get; private set; }

    public string? Read()
    {
        if (!File.Exists(PathName)) return null;
        try
        {
            var bytes = File.ReadAllBytes(PathName);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        {
            ReadFailed = true;
            return null;
        }
    }

    public void Write(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
        var temporary = PathName + ".new";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, PathName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Clear()
    {
        if (File.Exists(PathName)) File.Delete(PathName);
    }
}
