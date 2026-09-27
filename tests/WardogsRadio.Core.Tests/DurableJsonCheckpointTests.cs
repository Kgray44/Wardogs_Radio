using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class DurableJsonCheckpointTests
{
    sealed record SetupState(string OriginalDevice, string AppliedDevice, bool Confirmed);

    [Fact]
    public void SetupTransactionSurvivesRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wardogs-checkpoint-test-" + Guid.NewGuid());
        var path = Path.Combine(directory, "setup-visit-recovery.json");
        try
        {
            var firstProcess = new DurableJsonCheckpoint<SetupState>(path);
            firstProcess.Save(new SetupState("headset", "speakers", false));

            var nextProcess = new DurableJsonCheckpoint<SetupState>(path);
            Assert.True(nextProcess.Exists);
            Assert.Equal(new SetupState("headset", "speakers", false), nextProcess.Load());

            nextProcess.Clear();
            Assert.False(firstProcess.Exists);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
