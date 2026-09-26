using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class MacroHealthTests
{
    [Fact]
    public void MissingStationIsBrokenAndUnavailableProviderIsWarning()
    {
        var station = new Station { Source = "source" };
        var macro = new RadioMacro { Actions = [new() { Kind = ActionKind.ActivateStation, StationId = station.Id }] };
        var profile = new RadioProfile { Stations = [station], Macros = [macro] };
        Assert.Equal(MacroHealthState.Warning, MacroHealthEvaluator.Evaluate(macro, profile, _ => false).State);
        profile.Stations.Clear();
        Assert.Equal(MacroHealthState.Broken, MacroHealthEvaluator.Evaluate(macro, profile, _ => true).State);
    }

    [Fact]
    public void KeyboardConflictsAndUnavailableReleaseAreBroken()
    {
        var macro = new RadioMacro { Activation = MacroActivation.Hold, KeyboardBindings = ["F8"], Actions = [new() { Kind = ActionKind.Next }], ReleaseActions = [new() { Kind = ActionKind.Previous }] };
        var profile = new RadioProfile { Macros = [macro, new RadioMacro { KeyboardBindings = ["F8"] }] };
        Assert.Equal(MacroHealthState.Broken, MacroHealthEvaluator.Evaluate(macro, profile, _ => true).State);
        profile.Macros.RemoveAt(1);
        Assert.Equal(MacroHealthState.Broken, MacroHealthEvaluator.Evaluate(macro, profile, _ => true, releaseAvailable: false).State);
        Assert.Equal(MacroHealthState.Ready, MacroHealthEvaluator.Evaluate(macro, profile, _ => true).State);
    }

    [Fact]
    public void DisabledAndRunningStatesAreExplicit()
    {
        var macro = new RadioMacro { Enabled = false };
        var profile = new RadioProfile { Macros = [macro] };
        Assert.Equal(MacroHealthState.Disabled, MacroHealthEvaluator.Evaluate(macro, profile, _ => true).State);
        macro.Enabled = true;
        Assert.Equal(MacroHealthState.Running, MacroHealthEvaluator.Evaluate(macro, profile, _ => true, running: true).State);
    }
}
