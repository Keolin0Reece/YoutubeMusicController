namespace YoutubeMusicController.Api.Models;

public sealed record SearchResult(
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    string? Duration,
    string? ImageSrc,
    string VideoId,
    string MediaType);
