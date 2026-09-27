using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

public sealed class YoutubeMusicService
{
    private const string SongsSearchParams =
        "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";

    private readonly HttpClient _client;

    public YoutubeMusicService(IHttpClientFactory httpClientFactory)
    {
        _client = httpClientFactory.CreateClient("Ytmd");
    }

    public async Task<NowPlaying?> GetNowPlayingAsync(
        CancellationToken cancellationToken = default)
    {
        return await _client.GetFromJsonAsync<NowPlaying>(
            "/api/v1/song",
            cancellationToken);
    }

    public async Task<bool> IsConnectedAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetAsync(
                "/api/v1/song",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<SearchResponse> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.Params) ||
            !string.IsNullOrWhiteSpace(request.Continuation) ||
            string.IsNullOrWhiteSpace(request.Query))
        {
            return await ExecuteSearchAsync(request, cancellationToken);
        }

        var songsRequest = request with { Params = SongsSearchParams };
        var songsAttempt = await TrySearchAsync(
            songsRequest,
            cancellationToken);
        var alternateQuery = GetConnectorlessQuery(request.Query);
        var alternateSongsAttempt = alternateQuery is null
            ? null
            : await TrySearchAsync(
                songsRequest with { Query = alternateQuery },
                cancellationToken);
        var broadAttempt = await TrySearchAsync(request, cancellationToken);

        if (songsAttempt.Response is null &&
            broadAttempt.Response is null &&
            alternateSongsAttempt?.Response is null)
        {
            throw new HttpRequestException(
                "YouTube Music search failed.",
                new AggregateException(
                    new[]
                    {
                        songsAttempt.Error,
                        alternateSongsAttempt?.Error,
                        broadAttempt.Error
                    }
                        .OfType<Exception>()));
        }

        var mergedResults = YoutubeMusicSearchRanker.MergeAndRank(
            request.Query,
            (songsAttempt.Response?.Results ?? [])
                .Concat(alternateSongsAttempt?.Response?.Results ?? []),
            broadAttempt.Response?.Results ?? []);

        return new SearchResponse(
            mergedResults,
            songsAttempt.Response?.Continuation ??
                broadAttempt.Response?.Continuation);
    }

    private static string? GetConnectorlessQuery(string query)
    {
        const string Connector = " by ";
        var connectorIndex = query.LastIndexOf(
            Connector,
            StringComparison.OrdinalIgnoreCase);

        if (connectorIndex <= 0 ||
            connectorIndex + Connector.Length >= query.Length)
        {
            return null;
        }

        var title = query[..connectorIndex].Trim();
        var artist = query[(connectorIndex + Connector.Length)..].Trim();

        return title.Length > 0 && artist.Length > 0
            ? $"{title} {artist}"
            : null;
    }

    private async Task<SearchAttempt> TrySearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return new SearchAttempt(
                await ExecuteSearchAsync(request, cancellationToken),
                null);
        }
        catch (HttpRequestException ex)
        {
            return new SearchAttempt(null, ex);
        }
    }

    private async Task<SearchResponse> ExecuteSearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, string>();

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            payload["query"] = request.Query;
        }

        if (!string.IsNullOrWhiteSpace(request.Params))
        {
            payload["params"] = request.Params;
        }

        if (!string.IsNullOrWhiteSpace(request.Continuation))
        {
            payload["continuation"] = request.Continuation;
        }

        using var response = await _client.PostAsJsonAsync(
            "/api/v1/search",
            payload,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            contentStream,
            cancellationToken: cancellationToken);

        return YoutubeMusicSearchParser.Parse(document);
    }

    private sealed record SearchAttempt(
        SearchResponse? Response,
        HttpRequestException? Error);

    public async Task AddToQueueAsync(
        string videoId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/queue",
            new
            {
                videoId,
                insertPosition = "INSERT_AT_END"
            },
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task<QueueResponse> GetQueueAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(
            "/api/v1/queue",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return new QueueResponse([], null, false, false);
        }

        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            contentStream,
            cancellationToken: cancellationToken);

        return YoutubeMusicQueueParser.Parse(document);
    }

    public async Task RemoveQueueItemAsync(
        int index,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.DeleteAsync(
            $"/api/v1/queue/{index}",
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task MoveQueueItemAsync(
        int index,
        int toIndex,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/queue/{index}")
        {
            Content = JsonContent.Create(new { toIndex })
        };
        using var response = await _client.SendAsync(
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task ClearQueueAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.DeleteAsync(
            "/api/v1/queue",
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task TogglePlayPauseAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _client.PostAsync(
            "/api/v1/toggle-play",
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task NextAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _client.PostAsync(
            "/api/v1/next",
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task PreviousAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _client.PostAsync(
            "/api/v1/previous",
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
