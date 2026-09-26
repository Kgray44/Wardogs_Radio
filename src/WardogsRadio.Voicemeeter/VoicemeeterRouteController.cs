using WardogsRadio.Core;

namespace WardogsRadio.Voicemeeter;

/// <summary>Only touches the explicitly selected music strip. Reads before writes so macros can restore exact route state.</summary>
public sealed class VoicemeeterRouteController(IVoicemeeterRemote remote)
{
    public bool TryRead(int strip, string route, out bool enabled)
    {
        enabled = false;
        if (!TryParameter(strip, route, out var name)) return false;
        if (!remote.TryGetParameterFloat(name, out var value)) return false;
        enabled = value >= .5f;
        return true;
    }

    public string Change(int strip, string route, bool? enabled, MacroExecutionContext context)
    {
        var name = Parameter(strip, route);
        if (!remote.TryGetParameterFloat(name, out var previous))
            throw new InvalidOperationException($"Voicemeeter {name} is unavailable; no route changed.");
        var next = enabled ?? previous < .5f;
        if (!remote.TrySetParameterFloat(name, next ? 1 : 0))
            throw new InvalidOperationException($"Voicemeeter refused the {route} route change.");
        context.Remember($"route:{name}", previous);
        return $"{route} {(next ? "enabled" : "disabled")} on music strip {strip}";
    }

    public string Restore(int strip, string route, MacroExecutionContext context)
    {
        var name = Parameter(strip, route);
        if (!context.TryPeek<float>($"route:{name}", out var previous))
            throw new InvalidOperationException($"No previous {route} route state is available for this macro run.");
        if (!remote.TrySetParameterFloat(name, previous))
            throw new InvalidOperationException($"Voicemeeter refused to restore the {route} route.");
        context.TryRestore<float>($"route:{name}", out _);
        return $"{route} route restored to {(previous >= .5f ? "on" : "off")}";
    }

    static string Parameter(int strip, string route) => TryParameter(strip, route, out var name) ? name :
        throw new InvalidOperationException("Choose a valid Voicemeeter music strip and route first.");

    static bool TryParameter(int strip, string route, out string name)
    {
        name = "";
        if (strip is < 0 or > 7 || route is not ("A1" or "B1" or "B2" or "B3")) return false;
        name = $"Strip[{strip}].{route}";
        return true;
    }
}
