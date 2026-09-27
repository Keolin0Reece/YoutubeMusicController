namespace YoutubeMusicController.Api.Models;

public sealed record QueueResponse(
    IReadOnlyList<QueueItem> Items,
    int? SelectedIndex,
    bool AutoPlaying,
    bool HasMore);
