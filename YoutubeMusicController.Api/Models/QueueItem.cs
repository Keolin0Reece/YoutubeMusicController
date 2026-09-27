namespace YoutubeMusicController.Api.Models;

public sealed record QueueItem(
    int Index,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    string? Duration,
    string? ImageSrc,
    string VideoId,
    bool IsSelected,
    bool CanReorder);
