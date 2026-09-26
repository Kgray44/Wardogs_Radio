namespace WardogsRadio.Core;

/// <summary>One Hold/Momentary macro can be pressed by several bindings at once.</summary>
public sealed class MacroHeldSourceTracker
{
    readonly Dictionary<Guid, HashSet<string>> _sources = [];

    public bool Press(Guid macroId, string source)
    {
        if (!_sources.TryGetValue(macroId, out var held)) _sources[macroId] = held = new(StringComparer.OrdinalIgnoreCase);
        return held.Add(source) && held.Count == 1;
    }

    public bool Release(Guid macroId, string source)
    {
        if (!_sources.TryGetValue(macroId, out var held) || !held.Remove(source)) return false;
        if (held.Count > 0) return false;
        _sources.Remove(macroId);
        return true;
    }

    public bool HasSource(Guid macroId, string source) =>
        _sources.TryGetValue(macroId, out var held) && held.Contains(source);

    public void Forget(Guid macroId) => _sources.Remove(macroId);
}
