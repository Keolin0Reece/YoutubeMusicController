namespace YoutubeMusicController.Api.Models;

public sealed record DiscordConfigurationRequest(
    ulong ApplicationId,
    ulong? GuildId,
    string BotToken);
