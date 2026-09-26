using WardogsRadio.Update;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class ReleaseUpdateTests
{
    const string Valid = """{"schema":1,"channel":"stable","version":"0.10.0","tag":"v0.10.0","installer":"WARDOGS-Radio-Setup-v0.10.0.exe","installer_url":"https://github.com/Kgray44/Wardogs_Radio/releases/download/v0.10.0/WARDOGS-Radio-Setup-v0.10.0.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","minimum_launcher_version":"0.1.0","published_utc":"2026-09-26T00:00:00Z","release_notes_url":"https://github.com/Kgray44/Wardogs_Radio/releases/tag/v0.10.0"}""";

    [Theory][InlineData("0.10.0", "0.9.9")][InlineData("1.0.0", "0.99.0")][InlineData("0.1.0", "0.1.0")]
    public void ComparesVersionsNumerically(string leftText, string rightText)
    { Assert.True(SemanticVersion.TryParse(leftText, out var left)); Assert.True(SemanticVersion.TryParse(rightText, out var right)); Assert.Equal(Math.Sign(leftText == rightText ? 0 : 1), Math.Sign(left.CompareTo(right))); }

    [Fact] public void AcceptsOfficialStableManifest() => Assert.True(ReleaseManifestValidator.TryValidate(Valid, out var manifest, out _), "Expected official manifest to validate.");
    [Theory]
    [InlineData("\"schema\":1", "\"schema\":2")]
    [InlineData("\"channel\":\"stable\"", "\"channel\":\"beta\"")]
    [InlineData("\"tag\":\"v0.10.0\"", "\"tag\":\"v0.9.9\"")]
    [InlineData("WARDOGS-Radio-Setup-v0.10.0.exe", "evil.exe")]
    [InlineData("https://github.com/Kgray44/Wardogs_Radio/releases/download/v0.10.0/WARDOGS-Radio-Setup-v0.10.0.exe", "https://evil.example/installer.exe")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bad")]
    public void RejectsUnsafeManifestData(string oldText, string newText)
        => Assert.False(ReleaseManifestValidator.TryValidate(Valid.Replace(oldText, newText, StringComparison.Ordinal), out _, out _));

    [Fact]
    public void CurrentOlderAndUnavailableManifestsLaunchCurrent()
    {
        var local = new SemanticVersion(0, 10, 0); var launcher = new SemanticVersion(0, 10, 0);
        Assert.Equal(UpdateDecision.LaunchCurrent, ReleaseManifestValidator.Decide(false, null, local, launcher, out _, out _));
        Assert.Equal(UpdateDecision.LaunchCurrent, ReleaseManifestValidator.Decide(true, Valid, local, launcher, out _, out _));
        Assert.Equal(UpdateDecision.InstallUpdate, ReleaseManifestValidator.Decide(true, Valid, new SemanticVersion(0, 9, 9), launcher, out _, out _));
    }

    [Fact]
    public void RejectsLauncherMinimumMismatch()
    {
        var strict = Valid.Replace("\"minimum_launcher_version\":\"0.1.0\"", "\"minimum_launcher_version\":\"0.2.0\"");
        Assert.Equal(UpdateDecision.LaunchCurrent, ReleaseManifestValidator.Decide(true, strict, new SemanticVersion(0, 1, 0), new SemanticVersion(0, 1, 0), out _, out _));
    }
}
