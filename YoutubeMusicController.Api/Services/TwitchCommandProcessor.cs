using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

public sealed partial class TwitchCommandProcessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SongRepeatCooldown _songRepeatCooldown;
    private readonly ILogger<TwitchCommandProcessor> _logger;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset>
        _userSongCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _songRequestsEnabled;
    private readonly TimeSpan _userCooldown;
    private readonly TimeSpan _globalCooldown;
    private readonly int _maxQueryLength;
    private DateTimeOffset _lastSongRequestAt = DateTimeOffset.MinValue;

    public TwitchCommandProcessor(
        IServiceScopeFactory scopeFactory,
        SongRepeatCooldown songRepeatCooldown,
        IConfiguration configuration,
        ILogger<TwitchCommandProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _songRepeatCooldown = songRepeatCooldown;
        _logger = logger;
        _songRequestsEnabled = configuration.GetValue(
            "Twitch:Commands:SongRequestsEnabled",
            true);
        _userCooldown = TimeSpan.FromSeconds(Math.Max(
            0,
            configuration.GetValue(
                "Twitch:Commands:UserCooldownSeconds",
                30)));
        _globalCooldown = TimeSpan.FromSeconds(Math.Max(
            0,
            configuration.GetValue(
                "Twitch:Commands:GlobalCooldownSeconds",
                2)));
        _maxQueryLength = Math.Clamp(
            configuration.GetValue(
                "Twitch:Commands:MaxQueryLength",
                150),
            20,
            300);
    }

    public async Task<string?> ProcessAsync(
        TwitchChatMessage chatMessage,
        CancellationToken cancellationToken)
    {
        var match = CommandPattern().Match(chatMessage.Message.Trim());

        if (!match.Success)
        {
            return null;
        }

        var command = match.Groups["command"].Value.ToLowerInvariant();
        var arguments = match.Groups["arguments"].Value.Trim();

        await _commandGate.WaitAsync(cancellationToken);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var youtubeMusicService = scope.ServiceProvider
                .GetRequiredService<YoutubeMusicService>();

            return command switch
            {
                "song" or "sr" => await HandleSongRequestAsync(
                    chatMessage,
                    arguments,
                    youtubeMusicService,
                    cancellationToken),
                "nowplaying" or "np" => await HandleNowPlayingAsync(
                    youtubeMusicService,
                    cancellationToken),
                "queue" => await HandleQueueAsync(
                    youtubeMusicService,
                    cancellationToken),
                _ => null
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Twitch command {Command} could not reach YouTube Music.",
                command);
            return "YouTube Music is unavailable right now.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Twitch command {Command} failed.",
                command);
            return "I couldn't process that command right now.";
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task<string> HandleSongRequestAsync(
        TwitchChatMessage chatMessage,
        string query,
        YoutubeMusicService youtubeMusicService,
        CancellationToken cancellationToken)
    {
        if (!_songRequestsEnabled)
        {
            return "Song requests are currently disabled.";
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return $"@{chatMessage.DisplayName} use !song <title or artist>.";
        }

        if (query.Length > _maxQueryLength)
        {
            return $"@{chatMessage.DisplayName} that search is too long.";
        }

        var now = DateTimeOffset.UtcNow;

        if (!chatMessage.IsBroadcaster && !chatMessage.IsModerator)
        {
            if (_userSongCooldowns.TryGetValue(
                    chatMessage.UserId,
                    out var userLastRequestedAt))
            {
                var userRemaining = _userCooldown - (now - userLastRequestedAt);

                if (userRemaining > TimeSpan.Zero)
                {
                    return $"@{chatMessage.DisplayName} wait {Math.Ceiling(userRemaining.TotalSeconds)}s before requesting another song.";
                }
            }

            var globalRemaining = _globalCooldown - (now - _lastSongRequestAt);

            if (globalRemaining > TimeSpan.Zero)
            {
                return $"@{chatMessage.DisplayName} song requests are cooling down for {Math.Ceiling(globalRemaining.TotalSeconds)}s.";
            }
        }

        _lastSongRequestAt = now;

        var search = await youtubeMusicService.SearchAsync(
            new SearchRequest(query),
            cancellationToken);
        var result = search.Results.FirstOrDefault(item =>
            item.MediaType == "Song");

        if (result is null)
        {
            return $"@{chatMessage.DisplayName} I couldn't find a playable song for “{Sanitize(query)}”.";
        }

        if (!_songRepeatCooldown.TryReserve(
                result.VideoId,
                out var reservation,
                out var repeatRemaining))
        {
            return $"@{chatMessage.DisplayName} “{Sanitize(result.Title)}”: {_songRepeatCooldown.GetBlockedMessage(repeatRemaining)}";
        }

        try
        {
            await youtubeMusicService.AddToQueueAsync(
                result.VideoId,
                cancellationToken);
        }
        catch
        {
            _songRepeatCooldown.Cancel(reservation);
            throw;
        }

        _userSongCooldowns[chatMessage.UserId] = now;

        QueueItem? queuedItem = null;

        try
        {
            var queueAfter = await youtubeMusicService.GetQueueAsync(
                cancellationToken);
            queuedItem = queueAfter.Items.LastOrDefault(item =>
                item.VideoId == result.VideoId);
        }
        finally
        {
            _songRepeatCooldown.ConfirmQueued(
                reservation,
                queuedItem?.InstanceId);
        }
        var artist = result.Artists.FirstOrDefault() ?? "Unknown artist";
        var position = queuedItem is null
            ? string.Empty
            : $" at position {queuedItem.Index + 1}";

        return $"@{chatMessage.DisplayName} added “{Sanitize(result.Title)}” — {Sanitize(artist)}{position}.";
    }

    private static async Task<string> HandleNowPlayingAsync(
        YoutubeMusicService youtubeMusicService,
        CancellationToken cancellationToken)
    {
        var nowPlaying = await youtubeMusicService.GetNowPlayingAsync(
            cancellationToken);

        return string.IsNullOrWhiteSpace(nowPlaying?.Title)
            ? "Nothing is playing right now."
            : $"Now playing: “{Sanitize(nowPlaying.Title)}” — {Sanitize(nowPlaying.Artist ?? "Unknown artist")}.";
    }

    private static async Task<string> HandleQueueAsync(
        YoutubeMusicService youtubeMusicService,
        CancellationToken cancellationToken)
    {
        var queue = await youtubeMusicService.GetQueueAsync(cancellationToken);
        var upcoming = queue.Items
            .Where(item => !item.IsSelected)
            .Where(item => queue.SelectedIndex is null ||
                           item.Index > queue.SelectedIndex)
            .Take(3)
            .Select(item =>
                $"{item.Index + 1}. {Sanitize(item.Title)} — {Sanitize(item.Artists.FirstOrDefault() ?? "Unknown artist")}")
            .ToList();

        return upcoming.Count == 0
            ? "The upcoming queue is empty."
            : $"Up next: {string.Join(" | ", upcoming)}";
    }

    private static string Sanitize(string value)
    {
        var singleLine = WhitespacePattern().Replace(value, " ").Trim();
        return singleLine.Length <= 120
            ? singleLine
            : $"{singleLine[..117]}...";
    }

    [GeneratedRegex(
        @"^!(?<command>[a-z]+)(?:\s+(?<arguments>.*))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
