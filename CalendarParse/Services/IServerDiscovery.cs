namespace CalendarParse.Services;

/// <summary>
/// Returns the base URL of the CalendarParse API server.
/// Current implementation: manual IP:PORT from user preferences.
/// Future hook: mDNS / QR-code discovery (see TODOS.md).
/// </summary>
public interface IServerDiscovery
{
    /// <summary>Returns the base URL, e.g. "http://192.168.1.10:5150". Never null; may be empty if not configured.</summary>
    Task<string> GetBaseUrlAsync(CancellationToken ct = default);
}
