namespace YoutubeMusicController.Api.Models;

public sealed record DiscordConnectionStatus(
    bool Configured,
    bool Connected,
    string State,
    string? AccountName,
    string? Error);
