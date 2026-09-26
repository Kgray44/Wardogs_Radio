using System.Security.Cryptography;
using System.Text.Json;

namespace WardogsRadio.Update;

public readonly record struct SemanticVersion(int Major, int Minor, int Patch) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        var parts = text?.Split('.') ?? [];
        if (parts.Length != 3 || parts.Any(part => !int.TryParse(part, out var value) || value < 0)) return false;
        version = new(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        return true;
    }
    public int CompareTo(SemanticVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public sealed record ReleaseManifest(int Schema, string Channel, SemanticVersion Version, string Tag, string Installer,
    Uri InstallerUrl, string Sha256, SemanticVersion MinimumLauncherVersion, DateTimeOffset PublishedUtc, Uri ReleaseNotesUrl);

public static class ReleaseManifestValidator
{
    public const string Repository = "Kgray44/Wardogs_Radio";
    public const string LatestManifestUrl = "https://github.com/Kgray44/Wardogs_Radio/releases/latest/download/update-manifest.json";
    public static bool TryValidate(string json, out ReleaseManifest? manifest, out string error)
    {
        manifest = null; error = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("Manifest must be a JSON object.", out error);
            int schema = RequireInt(root, "schema");
            if (schema != 1) return Fail("Unsupported manifest schema.", out error);
            var channel = RequireString(root, "channel");
            if (channel != "stable") return Fail("Manifest channel is not stable.", out error);
            var versionText = RequireString(root, "version");
            if (!SemanticVersion.TryParse(versionText, out var version)) return Fail("Manifest version is malformed.", out error);
            var tag = RequireString(root, "tag");
            var installer = RequireString(root, "installer");
            if (tag != "v" + version || installer != $"WARDOGS-Radio-Setup-v{version}.exe") return Fail("Manifest tag or installer name is unsafe.", out error);
            var installerUrl = RequireUri(root, "installer_url");
            var expectedInstallerUrl = $"https://github.com/{Repository}/releases/download/{tag}/{installer}";
            if (!string.Equals(installerUrl.AbsoluteUri, expectedInstallerUrl, StringComparison.Ordinal)) return Fail("Installer URL is outside the official release namespace.", out error);
            var sha256 = RequireString(root, "sha256");
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return Fail("Manifest SHA-256 is malformed.", out error);
            var minimumText = RequireString(root, "minimum_launcher_version");
            if (!SemanticVersion.TryParse(minimumText, out var minimum)) return Fail("Minimum launcher version is malformed.", out error);
            var published = RequireString(root, "published_utc");
            if (!DateTimeOffset.TryParse(published, out var publishedUtc)) return Fail("Published time is malformed.", out error);
            var releaseNotes = RequireUri(root, "release_notes_url");
            if (releaseNotes.AbsoluteUri != $"https://github.com/{Repository}/releases/tag/{tag}") return Fail("Release notes URL is outside the official release namespace.", out error);
            manifest = new(schema, channel, version, tag, installer, installerUrl, sha256.ToLowerInvariant(), minimum, publishedUtc, releaseNotes);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        { error = exception.Message; return false; }
    }
    public static UpdateDecision Decide(bool fetchSucceeded, string? manifestJson, SemanticVersion localVersion, SemanticVersion launcherVersion, out ReleaseManifest? manifest, out string reason)
    {
        manifest = null;
        if (!fetchSucceeded || string.IsNullOrWhiteSpace(manifestJson)) { reason = "Update service unavailable."; return UpdateDecision.LaunchCurrent; }
        if (!TryValidate(manifestJson, out manifest, out reason)) return UpdateDecision.LaunchCurrent;
        if (launcherVersion.CompareTo(manifest!.MinimumLauncherVersion) < 0) { reason = "Installed launcher is below the required version."; return UpdateDecision.LaunchCurrent; }
        if (manifest.Version.CompareTo(localVersion) <= 0) { reason = "Installed version is current or newer."; return UpdateDecision.LaunchCurrent; }
        reason = "A newer stable release is available."; return UpdateDecision.InstallUpdate;
    }
    static string RequireString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException($"Required string '{name}' is missing.");
    static int RequireInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var integer) ? integer : throw new FormatException($"Required integer '{name}' is missing.");
    static Uri RequireUri(JsonElement root, string name)
    {
        var text = RequireString(root, name);
        return Uri.TryCreate(text, UriKind.Absolute, out var value) && value.Scheme == Uri.UriSchemeHttps ? value : throw new FormatException($"Required HTTPS URL '{name}' is malformed.");
    }
    static bool Fail(string message, out string error) { error = message; return false; }
}

public enum UpdateDecision { LaunchCurrent, InstallUpdate }

public static class PackageVerifier
{
    public static bool Verify(string packageRoot, string expectedVersion, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(packageRoot) || !Directory.Exists(packageRoot)) return Fail("Installation directory is unavailable.", out error);
        packageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        foreach (var file in new[] { "WARDOGS Radio.exe", "WARDOGS Radio Launcher.exe", "WARDOGS Radio Update Agent.exe", "VERSION", "PACKAGE_CONTENTS.sha256", "WARDOGS Radio.dll", "WARDOGS Radio.runtimeconfig.json", "WardogsRadio.Core.dll", "youtube-player.html" })
            if (!File.Exists(Path.Combine(packageRoot, file))) return Fail($"Required package file is missing: {file}", out error);
        var actualVersion = File.ReadAllText(Path.Combine(packageRoot, "VERSION")).Trim();
        if (!SemanticVersion.TryParse(actualVersion, out _) || actualVersion != expectedVersion) return Fail("Installed VERSION does not match the expected release.", out error);
        foreach (var line in File.ReadLines(Path.Combine(packageRoot, "PACKAGE_CONTENTS.sha256")))
        {
            var parts = line.Split(" *", 2, StringSplitOptions.None);
            if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)) return Fail("Package inventory is malformed.", out error);
            var file = Path.GetFullPath(Path.Combine(packageRoot, parts[1]));
            if (!file.StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return Fail("Package inventory references a missing or unsafe file.", out error);
            using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, parts[0], StringComparison.Ordinal)) return Fail($"Package file failed integrity verification: {parts[1]}", out error);
        }
        return true;
    }
    public static bool VerifySha256(string path, string expected) => File.Exists(path) && expected.Length == 64 && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(expected, StringComparison.OrdinalIgnoreCase);
    static bool Fail(string message, out string error) { error = message; return false; }
}
