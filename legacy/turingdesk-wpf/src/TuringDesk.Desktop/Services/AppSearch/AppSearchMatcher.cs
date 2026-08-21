using System.Globalization;
using System.Text;

namespace TuringDesk.Desktop.Services.AppSearch;

/// <summary>
/// Lightweight application-name matcher inspired by Flow Launcher's mature
/// acronym + fuzzy matching strategy. The implementation is intentionally
/// dependency-free and tuned for a tiny in-memory app catalogue.
/// </summary>
internal static class AppSearchMatcher
{
    public static int Score(string query, AppSearchEntry entry)
    {
        var normalizedQuery = NormalizeCompact(query);
        if (normalizedQuery.Length == 0) return 0;

        var best = 0;
        foreach (var alias in entry.Aliases)
        {
            if (alias.Length == 0) continue;
            best = Math.Max(best, ScoreAlias(normalizedQuery, alias));
        }

        return best;
    }

    private static int ScoreAlias(string query, string alias)
    {
        if (alias.Equals(query, StringComparison.Ordinal)) return 1600;
        if (alias.StartsWith(query, StringComparison.Ordinal))
            return 1380 - Math.Min(120, alias.Length - query.Length);

        var containsIndex = alias.IndexOf(query, StringComparison.Ordinal);
        if (containsIndex >= 0)
            return 1120 - Math.Min(260, containsIndex * 14 + Math.Max(0, alias.Length - query.Length));

        var acronym = BuildAcronym(alias);
        if (acronym.Equals(query, StringComparison.Ordinal)) return 1320;
        if (acronym.StartsWith(query, StringComparison.Ordinal))
            return 1200 - Math.Min(160, acronym.Length - query.Length);

        return ScoreSubsequence(query, alias);
    }

    /// <summary>
    /// Flow-style fuzzy subsequence score: all query characters must appear in
    /// order; contiguous characters, word boundaries and early matches are
    /// rewarded, while large gaps are penalized.
    /// </summary>
    private static int ScoreSubsequence(string query, string value)
    {
        var queryIndex = 0;
        var first = -1;
        var last = -1;
        var contiguousRun = 0;
        var longestRun = 0;
        var gapPenalty = 0;
        var boundaryBonus = 0;

        for (var index = 0; index < value.Length && queryIndex < query.Length; index++)
        {
            if (value[index] != query[queryIndex]) continue;

            if (first < 0) first = index;
            if (last >= 0)
            {
                var gap = index - last - 1;
                if (gap == 0)
                {
                    contiguousRun++;
                }
                else
                {
                    gapPenalty += Math.Min(40, gap * 4);
                    contiguousRun = 0;
                }
            }
            else
            {
                contiguousRun = 0;
            }

            longestRun = Math.Max(longestRun, contiguousRun + 1);
            if (IsBoundary(value, index)) boundaryBonus += 22;
            last = index;
            queryIndex++;
        }

        if (queryIndex != query.Length || first < 0) return 0;

        var span = last - first + 1;
        var score = 700;
        score += query.Length * 34;
        score += longestRun * 28;
        score += boundaryBonus;
        score -= Math.Min(260, first * 12);
        score -= Math.Min(220, Math.Max(0, span - query.Length) * 9);
        score -= gapPenalty;
        score -= Math.Min(100, Math.Max(0, value.Length - query.Length));
        return Math.Max(1, score);
    }

    internal static string NormalizeCompact(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var buffer = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character))
                buffer.Append(char.ToLowerInvariant(character));
        }
        return buffer.ToString();
    }

    internal static string BuildAcronymSource(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var buffer = new StringBuilder(value.Length * 2);
        var previousWasSeparator = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                buffer.Append(' ');
                previousWasSeparator = true;
                continue;
            }

            if (!previousWasSeparator && char.IsUpper(character)) buffer.Append(' ');
            buffer.Append(char.ToLowerInvariant(character));
            previousWasSeparator = false;
        }
        return buffer.ToString();
    }

    private static string BuildAcronym(string compactAlias)
    {
        // Pre-computed aliases are compact; acronym aliases are supplied separately
        // by AppSearchEntry, so this only keeps the first character as a fallback.
        return compactAlias.Length == 0 ? string.Empty : compactAlias[..1];
    }

    private static bool IsBoundary(string value, int index) =>
        index == 0 || !char.IsLetterOrDigit(value[index - 1]);
}
