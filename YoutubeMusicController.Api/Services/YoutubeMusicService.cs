using System.Net.Http.Json;
using System.Text.Json;
using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

public sealed class YoutubeMusicService
{
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
