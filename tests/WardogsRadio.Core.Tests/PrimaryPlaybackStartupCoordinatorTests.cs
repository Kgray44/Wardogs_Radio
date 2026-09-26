using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class PrimaryPlaybackStartupCoordinatorTests
{
    [Fact]
    public async Task SecondaryFeedCannotBlockPrimaryPlayback()
    {
        var coordinator = new PrimaryPlaybackStartupCoordinator();
        var primaryStarted = false;
        var secondaryStarted = false;
        var allowSecondaryToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await coordinator.StartAsync(
            () => { primaryStarted = true; return Task.CompletedTask; },
            async () => { secondaryStarted = true; await allowSecondaryToFinish.Task; });

        Assert.True(primaryStarted);
        Assert.True(secondaryStarted);
        allowSecondaryToFinish.SetResult();
    }

    [Fact]
    public async Task SecondaryFailureIsObservedWithoutFailingPrimaryPlayback()
    {
        var coordinator = new PrimaryPlaybackStartupCoordinator();
        Exception? observed = null;
        coordinator.SecondaryStartupFailed += (_, error) => observed = error;

        await coordinator.StartAsync(
            () => Task.CompletedTask,
            () => Task.FromException(new InvalidOperationException("B1 unavailable")));

        await Task.Yield();
        Assert.IsType<InvalidOperationException>(observed);
    }
}
