using System.Text.Json;
using System.Text.RegularExpressions;
using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

internal static partial class YoutubeMusicSearchParser
{
    private const string ArtistPageType = "MUSIC_PAGE_TYPE_ARTIST";
    private const string AlbumPageType = "MUSIC_PAGE_TYPE_ALBUM";

    public static SearchResponse Parse(JsonDocument document)
    {
        var results = new List<SearchResult>();
        var seenVideoIds = new HashSet<string>(StringComparer.Ordinal);

        if (TryGetSelectedSectionList(document.RootElement, out var sectionList) &&
            sectionList.TryGetProperty("contents", out var contents) &&
            contents.ValueKind == JsonValueKind.Array)
        {
            foreach (var section in contents.EnumerateArray())
            {
                AddSectionResults(section, results, seenVideoIds);
            }
        }

        return new SearchResponse(results, TryGetContinuation(sectionList));
    }

    private static void AddSectionResults(
        JsonElement section,
        List<SearchResult> results,
        HashSet<string> seenVideoIds,
        string? contextualArtist = null)
    {
        if (section.TryGetProperty("musicResponsiveListItemRenderer", out var directRenderer))
        {
            AddRenderer(directRenderer, results, seenVideoIds, contextualArtist);
        }

        if (section.TryGetProperty("musicShelfRenderer", out var shelf))
        {
            AddRendererContents(shelf, results, seenVideoIds, contextualArtist);
        }

        if (section.TryGetProperty("musicCardShelfRenderer", out var cardShelf))
        {
            AddRendererContents(
                cardShelf,
                results,
                seenVideoIds,
                GetCardArtist(cardShelf) ?? contextualArtist);
        }

        if (section.TryGetProperty("itemSectionRenderer", out var itemSection))
        {
            AddRendererContents(itemSection, results, seenVideoIds, contextualArtist);
        }
    }

    private static void AddRendererContents(
        JsonElement container,
        List<SearchResult> results,
        HashSet<string> seenVideoIds,
        string? contextualArtist)
    {
        if (!container.TryGetProperty("contents", out var contents) ||
            contents.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var content in contents.EnumerateArray())
        {
            AddSectionResults(content, results, seenVideoIds, contextualArtist);
        }
    }

    private static void AddRenderer(
        JsonElement renderer,
        List<SearchResult> results,
        HashSet<string> seenVideoIds,
        string? contextualArtist)
    {
        var videoId = GetVideoId(renderer);

        if (string.IsNullOrWhiteSpace(videoId) || !seenVideoIds.Add(videoId))
        {
            return;
        }

        var columns = GetFlexColumnRuns(renderer);
        var title = columns.Count > 0 ? JoinRunText(columns[0]) : null;

        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var metadataRuns = columns.Count > 1 ? columns[1] : [];
        var metadataLabel = GetFirstMeaningfulText(metadataRuns);
        var mediaType = IsPlayableMediaType(metadataLabel) ? metadataLabel! : "Song";
        var artists = metadataRuns
            .Where(run => GetPageType(run) == ArtistPageType)
            .Select(GetText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var albumRun = metadataRuns
            .FirstOrDefault(run => GetPageType(run) == AlbumPageType);

        if (artists.Count == 0)
        {
            var fallbackArtist = metadataRuns
                .Where(run => GetPageType(run) != AlbumPageType)
                .Select(GetText)
                .FirstOrDefault(text => IsLikelyArtist(text, mediaType));

            if (fallbackArtist is not null)
            {
                artists.Add(fallbackArtist);
            }
        }

        if (artists.Count == 0 && !string.IsNullOrWhiteSpace(contextualArtist))
        {
            artists.Add(contextualArtist);
        }

        results.Add(new SearchResult(
            title,
            artists,
            albumRun.ValueKind == JsonValueKind.Undefined ? null : GetText(albumRun),
            GetDuration(columns),
            GetThumbnailUrl(renderer),
            videoId,
            mediaType));
    }

    private static List<List<JsonElement>> GetFlexColumnRuns(JsonElement renderer)
    {
        var columns = new List<List<JsonElement>>();

        if (!renderer.TryGetProperty("flexColumns", out var flexColumns) ||
            flexColumns.ValueKind != JsonValueKind.Array)
        {
            return columns;
        }

        foreach (var column in flexColumns.EnumerateArray())
        {
            if (column.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var flexRenderer) &&
                flexRenderer.TryGetProperty("text", out var text) &&
                text.TryGetProperty("runs", out var runs) &&
                runs.ValueKind == JsonValueKind.Array)
            {
                columns.Add(runs.EnumerateArray().ToList());
            }
        }

        return columns;
    }

    private static string? GetVideoId(JsonElement renderer)
    {
        if (renderer.TryGetProperty("playlistItemData", out var playlistItemData) &&
            TryGetString(playlistItemData, "videoId", out var playlistVideoId))
        {
            return playlistVideoId;
        }

        if (renderer.TryGetProperty("flexColumns", out var flexColumns) &&
            TryFindStringProperty(flexColumns, "videoId", out var flexVideoId))
        {
            return flexVideoId;
        }

        if (renderer.TryGetProperty("overlay", out var overlay) &&
            TryFindStringProperty(overlay, "videoId", out var overlayVideoId))
        {
            return overlayVideoId;
        }

        return null;
    }

    private static string? GetThumbnailUrl(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("thumbnail", out var thumbnail) ||
            !TryFindArrayProperty(thumbnail, "thumbnails", out var thumbnails))
        {
            return null;
        }

        string? lastUrl = null;

        foreach (var item in thumbnails.EnumerateArray())
        {
            if (TryGetString(item, "url", out var url))
            {
                lastUrl = url;
            }
        }

        return lastUrl;
    }

    private static string? GetDuration(IEnumerable<List<JsonElement>> columns)
    {
        foreach (var run in columns.SelectMany(column => column))
        {
            var text = GetText(run)?.Trim();

            if (text is not null && DurationPattern().IsMatch(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? GetFirstMeaningfulText(IEnumerable<JsonElement> runs)
    {
        return runs
            .Select(GetText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text) && text.Trim() != "•");
    }

    private static string? GetCardArtist(JsonElement cardShelf)
    {
        if (!TryGetRuns(cardShelf, "subtitle", out var subtitleRuns) ||
            GetFirstMeaningfulText(subtitleRuns) != "Artist" ||
            !TryGetRuns(cardShelf, "title", out var titleRuns))
        {
            return null;
        }

        return JoinRunText(titleRuns);
    }

    private static bool TryGetRuns(
        JsonElement container,
        string propertyName,
        out List<JsonElement> runs)
    {
        runs = [];

        if (!container.TryGetProperty(propertyName, out var text) ||
            !text.TryGetProperty("runs", out var runsElement) ||
            runsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        runs = runsElement.EnumerateArray().ToList();
        return true;
    }

    private static bool IsPlayableMediaType(string? text) =>
        text is "Song" or "Video" or "Episode" or "Podcast";

    private static bool IsLikelyArtist(string? text, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();

        return value != "•" &&
               value != mediaType &&
               !DurationPattern().IsMatch(value) &&
               !Regex.IsMatch(value, @"^\d{4}$") &&
               !value.EndsWith(" plays", StringComparison.OrdinalIgnoreCase) &&
               !value.EndsWith(" views", StringComparison.OrdinalIgnoreCase) &&
               !value.Contains(" audience", StringComparison.OrdinalIgnoreCase);
    }

    private static string JoinRunText(IEnumerable<JsonElement> runs) =>
        string.Concat(runs.Select(GetText));

    private static string? GetText(JsonElement run) =>
        TryGetString(run, "text", out var text) ? text : null;

    private static string? GetPageType(JsonElement run)
    {
        if (!run.TryGetProperty("navigationEndpoint", out var navigationEndpoint) ||
            !navigationEndpoint.TryGetProperty("browseEndpoint", out var browseEndpoint) ||
            !browseEndpoint.TryGetProperty("browseEndpointContextSupportedConfigs", out var configs) ||
            !configs.TryGetProperty("browseEndpointContextMusicConfig", out var musicConfig))
        {
            return null;
        }

        return TryGetString(musicConfig, "pageType", out var pageType) ? pageType : null;
    }

    private static bool TryGetSelectedSectionList(
        JsonElement root,
        out JsonElement sectionList)
    {
        sectionList = default;

        if (!root.TryGetProperty("contents", out var contents) ||
            !contents.TryGetProperty("tabbedSearchResultsRenderer", out var tabbedResults) ||
            !tabbedResults.TryGetProperty("tabs", out var tabs) ||
            tabs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement? fallback = null;

        foreach (var tab in tabs.EnumerateArray())
        {
            if (!tab.TryGetProperty("tabRenderer", out var tabRenderer) ||
                !tabRenderer.TryGetProperty("content", out var tabContent) ||
                !tabContent.TryGetProperty("sectionListRenderer", out var candidate))
            {
                continue;
            }

            fallback ??= candidate;

            if (tabRenderer.TryGetProperty("selected", out var selected) &&
                selected.ValueKind == JsonValueKind.True)
            {
                sectionList = candidate;
                return true;
            }
        }

        if (fallback is { } fallbackValue)
        {
            sectionList = fallbackValue;
            return true;
        }

        return false;
    }

    private static string? TryGetContinuation(JsonElement sectionList)
    {
        if (sectionList.ValueKind == JsonValueKind.Undefined ||
            !sectionList.TryGetProperty("continuations", out var continuations) ||
            continuations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var continuation in continuations.EnumerateArray())
        {
            if (continuation.TryGetProperty("nextContinuationData", out var data) &&
                TryGetString(data, "continuation", out var token))
            {
                return token;
            }
        }

        return null;
    }

    private static bool TryFindStringProperty(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString()!;
                    return true;
                }

                if (TryFindStringProperty(property.Value, propertyName, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindStringProperty(item, propertyName, out value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindArrayProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        value = default;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(propertyName) &&
                property.Value.ValueKind == JsonValueKind.Array)
            {
                value = property.Value;
                return true;
            }

            if (TryFindArrayProperty(property.Value, propertyName, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;

        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    [GeneratedRegex(@"^\d{1,2}:\d{2}(?::\d{2})?$")]
    private static partial Regex DurationPattern();
}
