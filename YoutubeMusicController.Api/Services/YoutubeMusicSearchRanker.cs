using System.Text;
using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

internal static class YoutubeMusicSearchRanker
{
    public static IReadOnlyList<SearchResult> MergeAndRank(
        string query,
        IEnumerable<SearchResult> primaryResults,
        IEnumerable<SearchResult> fallbackResults)
    {
        var seenVideoIds = new HashSet<string>(StringComparer.Ordinal);
        var merged = primaryResults
            .Concat(fallbackResults)
            .Where(result => seenVideoIds.Add(result.VideoId))
            .Select((result, index) => new RankedResult(
                result,
                GetScore(query, result),
                index))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.OriginalIndex)
            .Select(item => item.Result)
            .ToList();

        return merged;
    }

    private static int GetScore(string query, SearchResult result)
    {
        var normalizedQuery = Normalize(query);
        var normalizedTitle = Normalize(result.Title);
        var normalizedArtists = result.Artists
            .Select(Normalize)
            .Where(artist => artist.Length > 0)
            .ToList();
        var queryWithoutBy = RemoveByConnector(normalizedQuery);
        var searchableText = string.Join(
            ' ',
            new[] { normalizedTitle }.Concat(normalizedArtists));
        var score = result.MediaType == "Song" ? 500 : 0;

        if (normalizedArtists.Any(artist =>
                normalizedQuery == $"{normalizedTitle} by {artist}" ||
                queryWithoutBy == $"{normalizedTitle} {artist}" ||
                queryWithoutBy == $"{artist} {normalizedTitle}"))
        {
            score += 10_000;
        }

        if (normalizedQuery == normalizedTitle)
        {
            score += 8_000;
        }

        if (normalizedArtists.Any(artist =>
                normalizedQuery.Contains(normalizedTitle, StringComparison.Ordinal) &&
                normalizedQuery.Contains(artist, StringComparison.Ordinal)))
        {
            score += 7_000;
        }

        var queryTokens = queryWithoutBy
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (queryTokens.Length > 0 &&
            queryTokens.All(token => ContainsToken(searchableText, token)))
        {
            score += 5_000;
        }

        if (normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal) ||
            normalizedQuery.Contains(normalizedTitle, StringComparison.Ordinal))
        {
            score += 2_000;
        }

        if (normalizedArtists.Any(artist =>
                artist.Contains(normalizedQuery, StringComparison.Ordinal) ||
                normalizedQuery.Contains(artist, StringComparison.Ordinal)))
        {
            score += 1_000;
        }

        return score;
    }

    private static bool ContainsToken(string text, string token) =>
        $" {text} ".Contains($" {token} ", StringComparison.Ordinal);

    private static string RemoveByConnector(string value) =>
        string.Join(
            ' ',
            value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token != "by"));

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = true;

        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private sealed record RankedResult(
        SearchResult Result,
        int Score,
        int OriginalIndex);
}
