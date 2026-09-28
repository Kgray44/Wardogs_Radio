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
        var applied = next ? 1f : 0f;
        if (!TryWriteVerified(name, applied))
            throw new InvalidOperationException($"Voicemeeter did not confirm the {route} route change.");
        context.Remember($"route:{name}", previous);
        context.Remember($"route-applied:{name}", applied);
        return $"{route} {(next ? "enabled" : "disabled")} on music strip {strip}";
    }

    public string Restore(int strip, string route, MacroExecutionContext context)
    {
        var name = Parameter(strip, route);
        if (!context.TryPeek<float>($"route:{name}", out var previous))
            throw new InvalidOperationException($"No previous {route} route state is available for this macro run.");
        if (!context.TryPeek<float>($"route-applied:{name}", out var applied) ||
            !remote.TryGetParameterFloat(name, out var current))
            throw new InvalidOperationException($"The {route} route cannot be checked before restoration.");
        if (Math.Abs(current - applied) >= .1f)
            throw new InvalidOperationException($"The {route} route changed outside WARDOGS; it was left untouched.");
        if (!TryWriteVerified(name, previous))
            throw new InvalidOperationException($"Voicemeeter did not confirm restoration of the {route} route.");
        context.TryRestore<float>($"route:{name}", out _);
        context.TryRestore<float>($"route-applied:{name}", out _);
        return $"{route} route restored to {(previous >= .5f ? "on" : "off")}";
    }

    bool TryWriteVerified(string name, float value)
    {
        if (!remote.TrySetParameterFloat(name, value)) return false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (remote.TryGetParameterFloat(name, out var observed) && Math.Abs(observed - value) < .1f) return true;
            Thread.Sleep(20);
        }
        return false;
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
