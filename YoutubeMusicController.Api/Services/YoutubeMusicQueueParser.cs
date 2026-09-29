using System.Text.Json;
using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

internal static class YoutubeMusicQueueParser
{
    private const string ArtistPageType = "MUSIC_PAGE_TYPE_ARTIST";
    private const string AlbumPageType = "MUSIC_PAGE_TYPE_ALBUM";

    public static QueueResponse Parse(JsonDocument document)
    {
        var items = new List<QueueItem>();
        var root = document.RootElement;

        if (root.TryGetProperty("items", out var itemElements) &&
            itemElements.ValueKind == JsonValueKind.Array)
        {
            var index = 0;

            foreach (var itemElement in itemElements.EnumerateArray())
            {
                if (TryParseItem(itemElement, index, out var item))
                {
                    items.Add(item);
                }

                index++;
            }
        }

        var selectedIndex = items
            .Where(item => item.IsSelected)
            .Select(item => (int?)item.Index)
            .FirstOrDefault();

        return new QueueResponse(
            items,
            selectedIndex,
            GetBoolean(root, "autoPlaying"),
            root.TryGetProperty("continuation", out var continuation) &&
            continuation.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(continuation.GetString()));
    }

    private static bool TryParseItem(
        JsonElement element,
        int index,
        out QueueItem item)
    {
        item = default!;

        if (!TryGetRenderer(element, out var renderer) ||
            !TryGetString(renderer, "videoId", out var videoId))
        {
            return false;
        }

        var title = GetRunText(renderer, "title");

        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var bylineRuns = GetRuns(renderer, "longBylineText");
        var artists = bylineRuns
            .Where(run => GetPageType(run) == ArtistPageType)
            .Select(GetText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var albumRun = bylineRuns
            .FirstOrDefault(run => GetPageType(run) == AlbumPageType);

        if (artists.Count == 0)
        {
            var fallbackArtist = GetRuns(renderer, "shortBylineText")
                .Select(GetText)
                .FirstOrDefault(text =>
                    !string.IsNullOrWhiteSpace(text) && text.Trim() != "•");

            if (fallbackArtist is not null)
            {
                artists.Add(fallbackArtist);
            }
        }

        item = new QueueItem(
            index,
            TryGetString(renderer, "playlistSetVideoId", out var instanceId)
                ? instanceId
                : null,
            title,
            artists,
            albumRun.ValueKind == JsonValueKind.Undefined ? null : GetText(albumRun),
            GetRunText(renderer, "lengthText"),
            GetThumbnailUrl(renderer),
            videoId,
            GetBoolean(renderer, "selected"),
            GetBoolean(renderer, "canReorder"));

        return true;
    }

    private static bool TryGetRenderer(
        JsonElement element,
        out JsonElement renderer)
    {
        renderer = default;

        if (element.TryGetProperty("playlistPanelVideoRenderer", out renderer))
        {
            return true;
        }

        return element.TryGetProperty("playlistPanelVideoWrapperRenderer", out var wrapper) &&
               wrapper.TryGetProperty("primaryRenderer", out var primaryRenderer) &&
               primaryRenderer.TryGetProperty("playlistPanelVideoRenderer", out renderer);
    }

    private static string? GetThumbnailUrl(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("thumbnail", out var thumbnail) ||
            !thumbnail.TryGetProperty("thumbnails", out var thumbnails) ||
            thumbnails.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? url = null;

        foreach (var entry in thumbnails.EnumerateArray())
        {
            if (TryGetString(entry, "url", out var candidate))
            {
                url = candidate;
            }
        }

        return url;
    }

    private static string? GetRunText(
        JsonElement renderer,
        string propertyName) =>
        string.Concat(GetRuns(renderer, propertyName).Select(GetText));

    private static List<JsonElement> GetRuns(
        JsonElement renderer,
        string propertyName)
    {
        if (!renderer.TryGetProperty(propertyName, out var text) ||
            !text.TryGetProperty("runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return runs.EnumerateArray().ToList();
    }

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

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.True;

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
}
