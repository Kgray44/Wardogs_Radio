namespace WardogsRadio.Core;

public static class StationCyclePolicy
{
    public static Station? Next(IEnumerable<Station> stations, Guid? activeStationId)
    {
        var enabled = stations.Where(station => station.Enabled)
            .OrderBy(station => station.Order)
            .ThenBy(station => station.Id)
            .ToList();
        if (enabled.Count == 0) return null;
        var index = activeStationId is null ? -1 : enabled.FindIndex(station => station.Id == activeStationId);
        return enabled[(index + 1) % enabled.Count];
    }
}
