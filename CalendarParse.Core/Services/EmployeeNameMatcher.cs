using CalendarParse.Models;

namespace CalendarParse.Services;

/// <summary>
/// Filters a list of shifts to the ones belonging to a specific employee.
///
/// Matching priority:
///   1. Case-insensitive exact match → auto-select
///   2. Levenshtein distance ≤ 2, accent-normalized → candidates list
///   3. No match → return all shifts (user claims manually)
///   4. Zero shifts in source → empty list (caller shows "No shifts found")
/// </summary>
public static class EmployeeNameMatcher
{
    public record MatchResult(
        List<ShiftData> Shifts,
        MatchKind Kind,
        List<string> Candidates);  // populated when Kind == Ambiguous

    public enum MatchKind
    {
        ExactMatch,     // single exact match found
        FuzzyMatch,     // single fuzzy match found
        Ambiguous,      // multiple fuzzy matches — user must disambiguate
        NoMatch,        // no match — show all shifts + claim prompt
        NoShifts,       // server returned zero shifts
    }

    public static MatchResult Match(List<ShiftData> allShifts, string employeeName)
    {
        if (allShifts.Count == 0)
            return new MatchResult([], MatchKind.NoShifts, []);

        if (string.IsNullOrWhiteSpace(employeeName))
            return new MatchResult(allShifts, MatchKind.NoMatch, []);

        var norm = Normalize(employeeName);

        // 1. Exact (case-insensitive, accent-normalized)
        var exactMatches = allShifts
            .Where(s => Normalize(s.Employee) == norm)
            .ToList();
        if (exactMatches.Count > 0)
            return new MatchResult(exactMatches, MatchKind.ExactMatch, []);

        // 2. Fuzzy — Levenshtein ≤ 2, per distinct employee name
        var employees  = allShifts.Select(s => s.Employee).Distinct().ToList();
        var candidates = employees
            .Where(e => Levenshtein(Normalize(e), norm) <= 2)
            .ToList();

        if (candidates.Count == 1)
        {
            var matched = allShifts.Where(s => s.Employee == candidates[0]).ToList();
            return new MatchResult(matched, MatchKind.FuzzyMatch, []);
        }

        if (candidates.Count > 1)
            return new MatchResult([], MatchKind.Ambiguous, candidates);

        // 3. No match
        return new MatchResult(allShifts, MatchKind.NoMatch, []);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Normalize(string s)
    {
        // Lower-case + decompose accents (é → e + combining acute) then strip non-ASCII combining chars
        var decomposed = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }

        return d[a.Length, b.Length];
    }
}
