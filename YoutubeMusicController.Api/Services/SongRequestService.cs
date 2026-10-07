using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

public sealed class SongRequestService(
    IServiceScopeFactory scopeFactory,
    SongRepeatCooldown songRepeatCooldown,
    IConfiguration configuration)
{
    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private readonly IReadOnlyDictionary<string, string> _preferredVideoIds =
        configuration
            .GetSection("QueuePolicy:PreferredVideos")
            .GetChildren()
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(
                item => NormalizeLookupKey(item.Key),
                item => item.Value!.Trim(),
                StringComparer.OrdinalIgnoreCase);

    public async Task<SongRequestOutcome> QueueAsync(
        string query,
        bool allowCloseVideoMatches,
        string? preferredVideoId = null,
        CancellationToken cancellationToken = default)
    {
        await _queueGate.WaitAsync(cancellationToken);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var youtubeMusicService = scope.ServiceProvider
                .GetRequiredService<YoutubeMusicService>();
            var search = await youtubeMusicService.SearchAsync(
                new SearchRequest(query),
                cancellationToken);
            SearchResult? result;

            var effectivePreferredVideoId = preferredVideoId;

            if (allowCloseVideoMatches &&
                string.IsNullOrWhiteSpace(effectivePreferredVideoId))
            {
                _preferredVideoIds.TryGetValue(
                    NormalizeLookupKey(query),
                    out effectivePreferredVideoId);
            }

            if (!string.IsNullOrWhiteSpace(effectivePreferredVideoId))
            {
                result = search.Results.FirstOrDefault(item =>
                    item.VideoId == effectivePreferredVideoId) ??
                    new SearchResult(
                        query,
                        [],
                        null,
                        null,
                        null,
                        effectivePreferredVideoId,
                        "Video");
            }
            else
            {
                result = allowCloseVideoMatches
                    ? search.Results.FirstOrDefault(item =>
                        item.MediaType is "Song" or "Video" &&
                        YoutubeMusicSearchRanker.IsCloseMatch(query, item))
                    : search.Results.FirstOrDefault(item =>
                        item.MediaType == "Song");
            }

            if (result is null)
            {
                return SongRequestOutcome.NotFound(query);
            }

            if (!songRepeatCooldown.TryReserve(
                    result.VideoId,
                    out var reservation,
                    out var repeatRemaining))
            {
                return SongRequestOutcome.Blocked(
                    query,
                    result,
                    songRepeatCooldown.GetBlockedMessage(repeatRemaining));
            }

            try
            {
                await youtubeMusicService.AddToQueueAsync(
                    result.VideoId,
                    cancellationToken);
            }
            catch
            {
                songRepeatCooldown.Cancel(reservation);
                throw;
            }

            QueueItem? queuedItem = null;

            try
            {
                var queueAfter = await youtubeMusicService.GetQueueAsync(
                    cancellationToken);
                queuedItem = queueAfter.Items.LastOrDefault(item =>
                    item.VideoId == result.VideoId);
            }
            finally
            {
                songRepeatCooldown.ConfirmQueued(
                    reservation,
                    queuedItem?.InstanceId);
            }

            return SongRequestOutcome.Added(query, result, queuedItem);
        }
        finally
        {
            _queueGate.Release();
        }
    }

    private static string NormalizeLookupKey(string value) =>
        string.Join(
            ' ',
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
}

public enum SongRequestFailure
{
    None,
    NotFound,
    RepeatBlocked
}

public sealed record SongRequestOutcome(
    string Query,
    SearchResult? SearchResult,
    QueueItem? QueueItem,
    SongRequestFailure Failure,
    string? FailureMessage)
{
    public bool WasAdded => Failure == SongRequestFailure.None;

    public string Title =>
        QueueItem?.Title ?? SearchResult?.Title ?? Query;

    public string Artist =>
        QueueItem?.Artists.FirstOrDefault() ??
        SearchResult?.Artists.FirstOrDefault() ??
        "Unknown artist";

    public int? Position => QueueItem is null
        ? null
        : QueueItem.Index + 1;

    public static SongRequestOutcome Added(
        string query,
        SearchResult searchResult,
        QueueItem? queueItem) =>
        new(query, searchResult, queueItem, SongRequestFailure.None, null);

    public static SongRequestOutcome NotFound(string query) =>
        new(query, null, null, SongRequestFailure.NotFound, null);

    public static SongRequestOutcome Blocked(
        string query,
        SearchResult searchResult,
        string message) =>
        new(
            query,
            searchResult,
            null,
            SongRequestFailure.RepeatBlocked,
            message);
}
