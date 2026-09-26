using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class MacroLibraryDraftTests
{
    [Fact]
    public void NewMacroAppearsOnDashboardByDefault()
    {
        var profile = new RadioProfile();
        var draft = new MacroLibraryDraft(profile);
        var macro = draft.Create();
        Assert.True(macro.ShowOnDashboard);
        draft.Commit();
        Assert.Contains(profile.Macros, x => x.Id == macro.Id && x.ShowOnDashboard);
    }

    [Fact]
    public void CreationAndCancelLeaveLiveProfileUnchanged()
    {
        var profile = new RadioProfile();
        var draft = new MacroLibraryDraft(profile);
        var created = draft.Create();
        created.Name = "Extraction";
        created.ShowOnDashboard = true;
        created.Actions.Add(new RadioAction { Kind = ActionKind.Next });
        Assert.Empty(profile.Macros);
    }

    [Fact]
    public void EditAndCommitPreserveIdentityButApplyChanges()
    {
        var original = new RadioMacro { Name = "Combat", Actions = [new() { Kind = ActionKind.Next }] };
        var profile = new RadioProfile { Macros = [original] };
        var draft = new MacroLibraryDraft(profile);
        draft.Macros[0].Name = "Extraction";
        draft.Macros[0].Actions.Add(new RadioAction { Kind = ActionKind.Delay, DelayMilliseconds = 25 });
        Assert.Equal("Combat", profile.Macros[0].Name);
        Assert.Single(profile.Macros[0].Actions);
        draft.Commit();
        Assert.Equal(original.Id, profile.Macros[0].Id);
        Assert.Equal("Extraction", profile.Macros[0].Name);
        Assert.Equal([ActionKind.Next, ActionKind.Delay], profile.Macros[0].Actions.Select(x => x.Kind));
    }

    [Fact]
    public void DuplicateDropsBindingsAndCopiesAllActionPhases()
    {
        var original = new RadioMacro
        {
            Name = "Comms", Activation = MacroActivation.Hold, KeyboardBindings = ["F8", "Ctrl+F8"],
            ControllerBindings = ["controller:a:button-22", "controller:b:pov-2"], ShowOnDashboard = true,
            Actions = [new() { Kind = ActionKind.SetMasterGain, Value = -24 }],
            ReleaseActions = [new() { Kind = ActionKind.RestorePreviousMasterGain }]
        };
        var draft = new MacroLibraryDraft(new RadioProfile { Macros = [original] });
        var copy = draft.Duplicate(draft.Macros[0]);
        Assert.Equal("Comms Copy", copy.Name);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Empty(copy.KeyboardBindings);
        Assert.Empty(copy.ControllerBindings);
        Assert.Single(copy.Actions);
        Assert.Single(copy.ReleaseActions);
        Assert.True(copy.ShowOnDashboard);
        copy.Actions[0].Value = -10;
        Assert.Equal(-24, draft.Macros[0].Actions[0].Value);
    }

    [Fact]
    public void DeleteAndMoveCommitOrderWithoutChangingStations()
    {
        var station = new Station();
        var first = new RadioMacro { Name = "First" };
        var second = new RadioMacro { Name = "Second" };
        var profile = new RadioProfile { Stations = [station], Macros = [first, second] };
        var draft = new MacroLibraryDraft(profile);
        draft.Move(draft.Macros[1], -1);
        draft.Delete(draft.Macros[1]);
        draft.Commit();
        Assert.Single(profile.Macros);
        Assert.Equal(second.Id, profile.Macros[0].Id);
        Assert.Equal(0, profile.Macros[0].Order);
        Assert.Same(station, profile.Stations[0]);
    }

    [Fact]
    public void CommitPreservesMultipleBindingsAndDashboardPinning()
    {
        var profile = new RadioProfile();
        var draft = new MacroLibraryDraft(profile);
        var macro = draft.Create();
        macro.Name = "Extraction";
        macro.KeyboardBindings.AddRange(["F8", "Ctrl+F8"]);
        macro.ControllerBindings.AddRange(["controller:a:button-22", "controller:b:pov-2"]);
        macro.ShowOnDashboard = true;
        draft.Commit();
        Assert.Equal(2, profile.Macros[0].KeyboardBindings.Count);
        Assert.Equal(2, profile.Macros[0].ControllerBindings.Count);
        Assert.True(profile.Macros[0].ShowOnDashboard);
    }

    [Fact]
    public void RenamingRaisesChangeForEditorLibrary()
    {
        var macro = new RadioMacro();
        string? changed = null;
        macro.PropertyChanged += (_, args) => changed = args.PropertyName;
        macro.Name = "Extraction";
        Assert.Equal(nameof(RadioMacro.Name), changed);
    }
}
