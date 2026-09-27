using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class ConfigurationSaveStateTests
{
    [Fact]
    public async Task FailedSaveRemainsObservableUntilARealSaveSucceeds()
    {
        var parent = Path.Combine(Path.GetTempPath(), "wardogs-save-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, "config-root");
        File.WriteAllText(root, "occupied");
        try
        {
            var store = new ConfigurationStore(root);
            var states = new List<ConfigurationSaveState>();
            store.SaveStateChanged += states.Add;
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(new AppConfiguration()));
            Assert.Equal([ConfigurationSaveState.Saving, ConfigurationSaveState.Failed], states);
            File.Delete(root);
            await store.SaveAsync(new AppConfiguration());
            Assert.Equal(ConfigurationSaveState.Saved, states.Last());
        }
        finally { Directory.Delete(parent, recursive: true); }
    }
}
