using System.Collections.ObjectModel;

namespace WardogsRadio.Core;

/// <summary>A detached editing session. Nothing in the live profile changes before Commit.</summary>
public sealed class MacroLibraryDraft
{
    readonly RadioProfile _target;
    public ObservableCollection<RadioMacro> Macros { get; }

    public MacroLibraryDraft(RadioProfile target)
    {
        _target = target;
        Macros = new(target.Macros.OrderBy(x => x.Order).Select(Clone));
    }

    public static RadioAction CopyAction(RadioAction action) => new()
    {
        Kind = action.Kind, StationId = action.StationId, Value = action.Value,
        DelayMilliseconds = action.DelayMilliseconds, Curve = action.Curve, Argument = action.Argument
    };

    static RadioMacro Clone(RadioMacro macro) => new()
    {
        Id = macro.Id, Name = macro.Name, Description = macro.Description, Glyph = macro.Glyph,
        IconId = macro.IconId, AccentColor = macro.AccentColor, Hotkey = macro.Hotkey,
        Enabled = macro.Enabled, ShowOnDashboard = macro.ShowOnDashboard, Activation = macro.Activation,
        FailurePolicy = macro.FailurePolicy, Order = macro.Order,
        KeyboardBindings = macro.KeyboardBindings.ToList(), ControllerBindings = macro.ControllerBindings.ToList(),
        Actions = macro.Actions.Select(CopyAction).ToList(), ReleaseActions = macro.ReleaseActions.Select(CopyAction).ToList(),
        OffActions = macro.OffActions.Select(CopyAction).ToList()
    };

    public RadioMacro Create()
    {
        var macro = new RadioMacro { Name = "New Macro", Order = Macros.Count, ShowOnDashboard = true };
        Macros.Add(macro);
        return macro;
    }

    public RadioMacro Duplicate(RadioMacro original)
    {
        var macro = Clone(original);
        macro.Id = Guid.NewGuid();
        macro.Name += " Copy";
        macro.Order = Macros.Count;
        macro.KeyboardBindings.Clear();
        macro.ControllerBindings.Clear();
        macro.Hotkey = null;
        Macros.Add(macro);
        return macro;
    }

    public void Move(RadioMacro macro, int offset)
    {
        var source = Macros.IndexOf(macro);
        var destination = source + offset;
        if (source < 0 || destination < 0 || destination >= Macros.Count) return;
        Macros.Move(source, destination);
    }

    public void Delete(RadioMacro macro) => Macros.Remove(macro);

    public void Commit()
    {
        for (var index = 0; index < Macros.Count; index++) Macros[index].Order = index;
        _target.Macros = Macros.ToList();
    }
}
