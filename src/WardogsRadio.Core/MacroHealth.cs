namespace WardogsRadio.Core;

public enum MacroHealthState { Ready, Disabled, Warning, Broken, Running }
public sealed record MacroHealth(MacroHealthState State, IReadOnlyList<string> Issues);

public static class MacroHealthEvaluator
{
    public static MacroHealth Evaluate(RadioMacro macro, RadioProfile profile,
        Func<RadioAction, bool> isOperational, bool keyboardAvailable = true,
        bool releaseAvailable = true, bool controllerAvailable = true, bool running = false)
    {
        if (!macro.Enabled) return new(MacroHealthState.Disabled, ["Macro is disabled"]);
        if (running) return new(MacroHealthState.Running, []);
        var errors = MacroDefinitionValidator.Validate(macro, profile).ToList();
        var warnings = new List<string>();
        var phases = new List<(string Name, List<RadioAction> Actions)> { ("Press", macro.Actions) };
        if (macro.Activation is MacroActivation.Hold or MacroActivation.Momentary)
            phases.Add(("Release", macro.ReleaseActions));
        if (macro.Activation == MacroActivation.Toggle)
            phases.Add(("Off", macro.OffActions));
        foreach (var (name, actions) in phases)
        {
            if (actions.Count == 0) warnings.Add($"{name} has no actions");
            foreach (var (action, index) in actions.Select((action, index) => (action, index)))
            {
                var definition = MacroActionCatalog.Get(action.Kind);
                if (MacroActionCatalog.RequiresStation(action.Kind))
                {
                    var station = profile.Stations.FirstOrDefault(x => x.Id == action.StationId);
                    if (station is null) continue;
                    if (!station.Enabled) { warnings.Add($"{name} action {index + 1}: {station.Name} disabled"); continue; }
                    if (string.IsNullOrWhiteSpace(station.Source)) { warnings.Add($"{name} action {index + 1}: {station.Name} needs a source"); continue; }
                }
                if (!isOperational(action)) warnings.Add($"{name} action {index + 1}: {definition.Name} needs an available output");
            }
        }
        var ownKeys = macro.KeyboardBindings.Count > 0 ? macro.KeyboardBindings : string.IsNullOrWhiteSpace(macro.Hotkey) ? [] : [macro.Hotkey];
        foreach (var key in ownKeys)
        {
            if (profile.Macros.Where(x => x.Id != macro.Id).Any(x => x.KeyboardBindings.Concat(string.IsNullOrWhiteSpace(x.Hotkey) ? [] : [x.Hotkey]).Contains(key, StringComparer.OrdinalIgnoreCase)) ||
                profile.Stations.Any(x => string.Equals(x.Hotkey, key, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"{key} conflicts with another binding");
        }
        if (!keyboardAvailable && ownKeys.Count > 0) errors.Add("Keyboard shortcut unavailable");
        if (!releaseAvailable && macro.Activation is MacroActivation.Hold or MacroActivation.Momentary && ownKeys.Count > 0)
            errors.Add("Key release could not be detected");
        if (!controllerAvailable && macro.ControllerBindings.Count > 0) warnings.Add("Controller binding unavailable");
        if (macro.ControllerBindings.Any(key => profile.Macros.Where(x => x.Id != macro.Id).Any(x => x.ControllerBindings.Contains(key, StringComparer.OrdinalIgnoreCase))))
            errors.Add("Controller binding conflict");
        return errors.Count > 0 ? new(MacroHealthState.Broken, errors.Concat(warnings).ToList()) :
            warnings.Count > 0 ? new(MacroHealthState.Warning, warnings) : new(MacroHealthState.Ready, []);
    }
}
