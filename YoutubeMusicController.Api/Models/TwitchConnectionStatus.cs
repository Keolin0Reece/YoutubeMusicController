namespace YoutubeMusicController.Api.Models;

public sealed record TwitchConnectionStatus(
    bool Configured,
    bool Authorized,
    bool Connected,
    string State,
    string? AccountName,
    string? Error);
