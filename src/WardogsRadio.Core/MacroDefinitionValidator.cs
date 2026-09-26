namespace WardogsRadio.Core;

public static class MacroDefinitionValidator
{
    static readonly string[] Pages = ["dashboard", "stations", "macros", "audio", "settings", "diagnostics"];

    public static IReadOnlyList<string> Validate(RadioMacro macro, RadioProfile profile)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(macro.Name)) errors.Add("Macro needs a name");
        if (macro.Actions.Count == 0) errors.Add("On Press needs at least one action");
        if (macro.Activation is MacroActivation.Hold or MacroActivation.Momentary && macro.ReleaseActions.Count == 0)
            errors.Add("On Release needs at least one action");
        if (macro.Activation == MacroActivation.Toggle && macro.OffActions.Count == 0)
            errors.Add("Off State needs at least one action");
        var phases = new List<(string Name, List<RadioAction> Actions)> { ("On Press", macro.Actions) };
        if (macro.Activation is MacroActivation.Hold or MacroActivation.Momentary) phases.Add(("On Release", macro.ReleaseActions));
        if (macro.Activation == MacroActivation.Toggle) phases.Add(("Off State", macro.OffActions));
        foreach (var (phase, actions) in phases)
        foreach (var (action, index) in actions.Select((value, offset) => (value, offset)))
        {
            var label = $"{phase} action {index + 1}";
            if (MacroActionCatalog.RequiresStation(action.Kind) && !profile.Stations.Any(x => x.Id == action.StationId))
                errors.Add($"{label}: choose an existing station");
            if (MacroActionCatalog.RequiresGain(action.Kind) &&
                (action.Value is not { } db || !double.IsFinite(db) || db < -60 || db > 12))
                errors.Add($"{label}: gain must be between -60 and +12 dB");
            if (MacroActionCatalog.HasDuration(action.Kind) && (action.DelayMilliseconds < 0 || action.DelayMilliseconds > 600_000))
                errors.Add($"{label}: duration must be between 0 and 600,000 ms");
            if (action.Kind == ActionKind.Seek && (action.Value is not { } seconds || !double.IsFinite(seconds) || seconds < 0))
                errors.Add($"{label}: choose a nonnegative position");
            if (action.Kind == ActionKind.LaunchApplication && string.IsNullOrWhiteSpace(action.Argument))
                errors.Add($"{label}: choose an application path");
            if (action.Kind == ActionKind.OpenPage && !Pages.Contains(action.Argument, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{label}: choose a WARDOGS Radio page");
            if (action.Kind == ActionKind.FadeToStation && !Enum.IsDefined(action.Curve))
                errors.Add($"{label}: choose a transition curve");
        }
        return errors;
    }
}
