using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class ConfigurationMutationPlaybackBoundaryTests
{
    [Fact]
    public void NeverTunedSessionDoesNotRequireStopOrDetach()
    {
        var state = new ConfigurationMutationPlaybackState();
        Assert.False(state.RequiresStopConfirmation);
        Assert.False(state.RequiresDetach);
    }

    [Fact]
    public void PausedLocalStationCanProceedButMustDetachItsPlayers()
    {
        var state = new ConfigurationMutationPlaybackState(true, false, true, true, false, false, false);
        Assert.False(state.RequiresStopConfirmation);
        Assert.True(state.RequiresDetach);
    }

    [Fact]
    public void PausedYouTubeStationCanProceedButMustDetachItsRouteAndFeed()
    {
        var state = new ConfigurationMutationPlaybackState(true, false, false, false, true, true, false);
        Assert.False(state.RequiresStopConfirmation);
        Assert.True(state.RequiresDetach);
    }

    [Fact]
    public void ActivePlaybackRequiresExplicitStopAndContinue()
    {
        var state = new ConfigurationMutationPlaybackState(true, true, true, true, false, false, false);
        Assert.True(state.RequiresStopConfirmation);
        Assert.True(state.RequiresDetach);
    }

    [Fact]
    public void RestoreUsesTheSamePausedStationDetachBoundaryAsImport()
    {
        // The MainWindow routes both Import and Restore through the same
        // PrepareConfigurationMutationAsync boundary. This policy assertion
        // protects the non-playing, already-tuned restore workflow.
        var pausedYouTube = new ConfigurationMutationPlaybackState(true, false, false, false, true, true, false);
        Assert.False(pausedYouTube.RequiresStopConfirmation);
        Assert.True(pausedYouTube.RequiresDetach);
    }
}
