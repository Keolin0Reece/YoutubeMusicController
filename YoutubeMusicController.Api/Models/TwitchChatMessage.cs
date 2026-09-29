namespace YoutubeMusicController.Api.Models;

public sealed record TwitchChatMessage(
    string Id,
    string UserId,
    string Username,
    string DisplayName,
    string Message,
    bool IsBroadcaster,
    bool IsModerator,
    DateTimeOffset ReceivedAt);
