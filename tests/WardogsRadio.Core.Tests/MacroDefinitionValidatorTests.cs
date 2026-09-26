using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class MacroDefinitionValidatorTests
{
    [Fact]
    public void CurrentStationTransportActionsNeedNoStationTarget()
    {
        foreach (var kind in new[] { ActionKind.PlayCurrentStation, ActionKind.PauseCurrentStation })
        {
            Assert.Equal(MacroActionParameter.None, MacroActionCatalog.Get(kind).Parameter);
            Assert.False(MacroActionCatalog.RequiresStation(kind));
            var macro = new RadioMacro { Name = "Transport", Actions = [new RadioAction { Kind = kind }] };
            Assert.Empty(MacroDefinitionValidator.Validate(macro, new RadioProfile()));
        }
    }
    [Fact]
    public void MacroCatalogOffersIndependentMasterLevelsAndRestores()
    {
        var macro = new RadioMacro
        {
            Actions =
            [
                new() { Kind = ActionKind.SetHeadsetMasterGain, Value = -12 },
                new() { Kind = ActionKind.SetGameMasterGain, Value = -18 }
            ]
        };
        var profile = new RadioProfile { Macros = [macro] };
        Assert.Empty(MacroDefinitionValidator.Validate(macro, profile));
        Assert.Contains("Headset", MacroActionCatalog.Get(ActionKind.SetHeadsetMasterGain).Name);
        Assert.Contains("Game", MacroActionCatalog.Get(ActionKind.SetGameMasterGain).Name);
        Assert.Equal(MacroActionParameter.None, MacroActionCatalog.Get(ActionKind.RestorePreviousHeadsetMasterGain).Parameter);
        Assert.Equal(MacroActionParameter.None, MacroActionCatalog.Get(ActionKind.RestorePreviousGameMasterGain).Parameter);
    }

    [Fact]
    public void StationGainNeedsBothExistingStationAndValidGain()
    {
        var station = new Station();
        var macro = new RadioMacro { Actions = [new() { Kind = ActionKind.SetStationGain, StationId = station.Id, Value = -10 }] };
        var profile = new RadioProfile { Stations = [station], Macros = [macro] };
        Assert.Empty(MacroDefinitionValidator.Validate(macro, profile));
        macro.Actions[0].Value = -80;
        Assert.Contains(MacroDefinitionValidator.Validate(macro, profile), x => x.Contains("gain"));
        macro.Actions[0].Value = -10;
        profile.Stations.Clear();
        Assert.Contains(MacroDefinitionValidator.Validate(macro, profile), x => x.Contains("station"));
    }

    [Fact]
    public void FadeCarriesStationDurationAndCurveThroughSerialization()
    {
        var station = new Station();
        var macro = new RadioMacro { Actions = [new() { Kind = ActionKind.FadeToStation, StationId = station.Id, DelayMilliseconds = 500, Curve = TransitionCurve.Smoothstep }] };
        var profile = new RadioProfile { Stations = [station], Macros = [macro] };
        Assert.Empty(MacroDefinitionValidator.Validate(macro, profile));
        var json = System.Text.Json.JsonSerializer.Serialize(macro);
        var copy = System.Text.Json.JsonSerializer.Deserialize<RadioMacro>(json)!;
        Assert.Equal(500, copy.Actions[0].DelayMilliseconds);
        Assert.Equal(TransitionCurve.Smoothstep, copy.Actions[0].Curve);
        Assert.Contains("500 ms", MacroActionCatalog.Summarize(copy.Actions[0], profile.Stations));
    }

    [Fact]
    public void EmptyAndInvalidActionsCannotBeCommitted()
    {
        var macro = new RadioMacro { Actions = [] };
        var profile = new RadioProfile { Macros = [macro] };
        Assert.NotEmpty(MacroDefinitionValidator.Validate(macro, profile));
        macro.Actions.Add(new RadioAction { Kind = ActionKind.OpenPage, Argument = "invented" });
        Assert.Contains(MacroDefinitionValidator.Validate(macro, profile), x => x.Contains("page"));
    }

    [Fact]
    public void TemporaryAndToggleMacrosRequireAReversingPhase()
    {
        var macro = new RadioMacro { Activation = MacroActivation.Hold, Actions = [new() { Kind = ActionKind.Next }] };
        var profile = new RadioProfile { Macros = [macro] };
        Assert.Contains(MacroDefinitionValidator.Validate(macro, profile), x => x.Contains("On Release"));
        macro.ReleaseActions.Add(new RadioAction { Kind = ActionKind.Previous });
        Assert.Empty(MacroDefinitionValidator.Validate(macro, profile));
        macro.Activation = MacroActivation.Toggle;
        Assert.Contains(MacroDefinitionValidator.Validate(macro, profile), x => x.Contains("Off State"));
    }
}
