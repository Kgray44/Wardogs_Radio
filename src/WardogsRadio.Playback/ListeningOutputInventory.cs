namespace WardogsRadio.Playback;

public static class ListeningOutputInventory
{
    public static WindowsAudioEndpoint? Selected(IReadOnlyList<WindowsAudioEndpoint> endpoints, string? selectedId) =>
        endpoints.FirstOrDefault(endpoint => string.Equals(endpoint.Id, selectedId, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<WindowsAudioEndpoint> IncludeSavedSelection(
        IReadOnlyList<WindowsAudioEndpoint> discovered, string? selectedId, string? selectedName)
    {
        var outputs = discovered.Where(endpoint => !endpoint.IsInput).ToList();
        if (!string.IsNullOrWhiteSpace(selectedId) &&
            !outputs.Any(endpoint => string.Equals(endpoint.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
            outputs.Add(new WindowsAudioEndpoint(selectedId, selectedName ?? "Saved listening output", false, false));
        return outputs;
    }
}
