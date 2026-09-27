namespace YoutubeMusicController.Api.Models;

public sealed record SearchRequest(
    string? Query,
    string? Params = null,
    string? Continuation = null);
