using System.Text.RegularExpressions;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace YoutubeMusicController.Api.Services;

public sealed partial class DiscordMusicModule(
    SongRequestService songRequestService,
    DiscordRequestCooldown requestCooldown,
    DiscordBotService botService,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DiscordMusicModule> logger)
    : InteractionModuleBase<SocketInteractionContext>
{
    private readonly int _maxQueryLength = Math.Clamp(
        configuration.GetValue("Discord:Commands:MaxQueryLength", 150),
        20,
        300);
    private readonly string? _videoRequesterUsername = configuration[
        "Discord:Commands:VideoRequesterUsername"]?.Trim();

    [SlashCommand("song", "Search for a song or video and add it to the queue.")]
    public async Task SongAsync(
        [Summary("query", "Song title and optionally the artist")]
        string query)
    {
        if (!await CheckAccessAsync())
        {
            return;
        }

        query = query.Trim();

        if (query.Length == 0 || query.Length > _maxQueryLength)
        {
            await RespondAsync(
                $"Enter a song query between 1 and {_maxQueryLength} characters.",
                ephemeral: true);
            return;
        }

        var bypassCooldown = Context.User is SocketGuildUser guildUser &&
            guildUser.GuildPermissions.ManageGuild;

        if (!requestCooldown.TryBegin(
                Context.User.Id,
                bypassCooldown,
                out var blockedMessage))
        {
            await RespondAsync(blockedMessage, ephemeral: true);
            return;
        }

        await DeferAsync();

        try
        {
            var allowCloseVideoMatches =
                !string.IsNullOrWhiteSpace(_videoRequesterUsername) &&
                string.Equals(
                    Context.User.Username,
                    _videoRequesterUsername,
                    StringComparison.OrdinalIgnoreCase);
            var outcome = await songRequestService.QueueAsync(
                query,
                allowCloseVideoMatches);

            if (outcome.Failure == SongRequestFailure.NotFound)
            {
                await ModifyOriginalResponseAsync(message =>
                    message.Content = $"I couldn't find a close playable match for **{Escape(query)}**.");
                return;
            }

            if (outcome.Failure == SongRequestFailure.RepeatBlocked)
            {
                await ModifyOriginalResponseAsync(message =>
                    message.Content = $"**{Escape(outcome.Title)}**: {outcome.FailureMessage}");
                return;
            }

            requestCooldown.Confirm(Context.User.Id);
            var position = outcome.Position is null
                ? string.Empty
                : $" at position {outcome.Position}";
            await ModifyOriginalResponseAsync(message =>
                message.Content = $"Added **{Escape(outcome.Title)}** — {Escape(outcome.Artist)}{position}.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Discord song request could not reach YouTube Music.");
            await ModifyOriginalResponseAsync(message =>
                message.Content = "YouTube Music is unavailable right now.");
        }
    }

    [SlashCommand("nowplaying", "Show the song currently playing.")]
    public async Task NowPlayingAsync()
    {
        if (!await CheckAccessAsync())
        {
            return;
        }

        await DeferAsync();
        using var scope = scopeFactory.CreateScope();
        var youtubeMusicService = scope.ServiceProvider
            .GetRequiredService<YoutubeMusicService>();
        var current = await youtubeMusicService.GetNowPlayingAsync();
        var message = string.IsNullOrWhiteSpace(current?.Title)
            ? "Nothing is playing right now."
            : $"Now playing: **{Escape(current.Title)}** — {Escape(current.Artist ?? "Unknown artist")}.";
        await ModifyOriginalResponseAsync(response => response.Content = message);
    }

    [SlashCommand("queue", "Show the next three songs in the queue.")]
    public async Task QueueAsync()
    {
        if (!await CheckAccessAsync())
        {
            return;
        }

        await DeferAsync();
        using var scope = scopeFactory.CreateScope();
        var youtubeMusicService = scope.ServiceProvider
            .GetRequiredService<YoutubeMusicService>();
        var queue = await youtubeMusicService.GetQueueAsync();
        var upcoming = queue.Items
            .Where(item => !item.IsSelected)
            .Where(item => queue.SelectedIndex is null ||
                           item.Index > queue.SelectedIndex)
            .Take(3)
            .Select(item =>
                $"{item.Index + 1}. **{Escape(item.Title)}** — {Escape(item.Artists.FirstOrDefault() ?? "Unknown artist")}")
            .ToList();
        var message = upcoming.Count == 0
            ? "The upcoming queue is empty."
            : $"Up next:\n{string.Join('\n', upcoming)}";
        await ModifyOriginalResponseAsync(response => response.Content = message);
    }

    private async Task<bool> CheckAccessAsync()
    {
        if (botService.IsInteractionAllowed(Context))
        {
            return true;
        }

        await RespondAsync(
            "This command is not enabled in this server or channel.",
            ephemeral: true);
        return false;
    }

    private static string Escape(string value)
    {
        var singleLine = WhitespacePattern().Replace(value, " ").Trim();
        singleLine = singleLine.Length <= 180
            ? singleLine
            : $"{singleLine[..177]}...";
        return singleLine
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("~", "\\~", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace(">", "\\>", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
