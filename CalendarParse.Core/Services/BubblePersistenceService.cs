using System.Text.Json;
using CalendarParse.Core.Models;

namespace CalendarParse.Core.Services;

/// <summary>
/// Serializes and deserializes the list of <see cref="BubblePersist"/> DTOs
/// that are stored in <c>ScheduleRun.ShiftsJson</c>.  Extracted from
/// ConfirmationPage so the round-trip logic can be unit-tested without MAUI.
/// </summary>
public static class BubblePersistenceService
{
    private static readonly JsonSerializerOptions _options =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>Serializes a list of persist DTOs to a JSON string.</summary>
    public static string Serialize(IEnumerable<BubblePersist> bubbles)
        => JsonSerializer.Serialize(bubbles.ToList());

    /// <summary>
    /// Deserializes a JSON string back to the list of persist DTOs.
    /// Returns an empty list for <see langword="null"/>, empty, or malformed input.
    /// Never throws.
    /// </summary>
    public static List<BubblePersist> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<BubblePersist>>(json, _options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
