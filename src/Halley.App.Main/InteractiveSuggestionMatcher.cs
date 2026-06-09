namespace Halley.App.Main;

public static class InteractiveSuggestionMatcher
{
    public static IReadOnlyList<InteractiveSuggestion> GetMatches(
        string? currentText,
        IReadOnlyList<InteractiveSuggestion> suggestions)
    {
        var needle = Normalize(currentText);
        if (needle is null)
        {
            return suggestions;
        }

        return suggestions
            .Select((suggestion, index) => new RankedSuggestion(suggestion, index, GetRank(needle, suggestion.Value)))
            .Where(item => item.Rank < int.MaxValue)
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Index)
            .Select(item => item.Suggestion)
            .ToArray();
    }

    private static int GetRank(string needle, string value)
    {
        var normalizedValue = Normalize(value);
        if (normalizedValue is null)
        {
            return int.MaxValue;
        }

        if (string.Equals(normalizedValue, needle, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (normalizedValue.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (normalizedValue.Split([' ', '-', '+', '_', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        if (normalizedValue.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return int.MaxValue;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record RankedSuggestion(InteractiveSuggestion Suggestion, int Index, int Rank);
}
