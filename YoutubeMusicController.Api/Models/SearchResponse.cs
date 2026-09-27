namespace YoutubeMusicController.Api.Models;

public sealed record SearchResponse(
    IReadOnlyList<SearchResult> Results,
    string? Continuation);
