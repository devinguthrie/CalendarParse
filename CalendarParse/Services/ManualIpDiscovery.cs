using CalendarParse.Data;

namespace CalendarParse.Services;

/// <summary>Reads IP:PORT from the persisted user preferences row.</summary>
public class ManualIpDiscovery(ScheduleHistoryDb db) : IServerDiscovery
{
    public async Task<string> GetBaseUrlAsync(CancellationToken ct = default)
    {
        var prefs = await db.GetPreferencesAsync(ct);
        return prefs.ServerUrl;
    }
}
