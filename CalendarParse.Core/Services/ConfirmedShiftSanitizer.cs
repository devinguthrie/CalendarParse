namespace CalendarParse.Core.Services;

/// <summary>
/// Normalizes confirmed shift text before it is persisted or submitted.
/// Defensive guard: blank selections must stay blank even if UI text is polluted
/// by employee-label state.
/// </summary>
public static class ConfirmedShiftSanitizer
{
    public static string NormalizeTimeRange(string? displayTime, string? employeeName)
    {
        var normalized = (displayTime ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var normalizedEmployee = (employeeName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedEmployee)
            && string.Equals(normalized, normalizedEmployee, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return normalized;
    }
}