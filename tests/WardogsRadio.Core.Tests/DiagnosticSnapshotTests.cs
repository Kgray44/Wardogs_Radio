using WardogsRadio.Core;
using WardogsRadio.Diagnostics;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class DiagnosticSnapshotTests
{
    [Fact]
    public async Task ExportUsesTheDisplayedSnapshotAndPreservesOptionalStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wardogs-diagnostic-test-" + Guid.NewGuid());
        try
        {
            var capturedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var readiness = new SystemReadinessSnapshot(OverallReadiness.NeedsAttention,
            [
                new("game-route", ReadinessCategory.GameVoice, ReadinessSeverity.NeedsAction, true,
                    "Radio feed", "The game feed is disconnected."),
                new("soundcloud", ReadinessCategory.Providers, ReadinessSeverity.Optional, false,
                    "SoundCloud", "Not configured.")
            ], capturedAt);
            var snapshot = new DiagnosticSnapshot(readiness,
                [new DiagnosticItem("Raw mixer", "INFO", "Captured at display time", "No audio content")]);
            var path = await DiagnosticService.ExportSnapshotAsync(snapshot, directory, "test-version");
            var report = await File.ReadAllTextAsync(path);
            Assert.Contains("Overall system health: NeedsAttention", report);
            Assert.Contains("The game feed is disconnected.", report);
            Assert.Contains("soundcloud / Optional", report);
            Assert.Contains("Captured at display time", report);
            Assert.Contains($"Generated: {capturedAt:O}", report);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
