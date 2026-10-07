using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

public sealed partial class TwitchCommandProcessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SongRequestService _songRequestService;
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
        SongRequestService songRequestService,
        IConfiguration configuration,
        ILogger<TwitchCommandProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _songRequestService = songRequestService;
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
        if (!TwitchCommandParser.TryParse(
                chatMessage.Message,
                out var parsed))
        {
            return null;
        }

        var command = parsed.Command;
        var arguments = parsed.Arguments;

        await _commandGate.WaitAsync(cancellationToken);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var youtubeMusicService = scope.ServiceProvider
                .GetRequiredService<YoutubeMusicService>();

            return command switch
            {
                "song" => await HandleSongRequestAsync(
                    chatMessage,
                    arguments,
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

        var outcome = await _songRequestService.QueueAsync(
            query,
            allowCloseVideoMatches: false,
            preferredVideoId: null,
            cancellationToken: cancellationToken);

        if (outcome.Failure == SongRequestFailure.NotFound)
        {
            return $"@{chatMessage.DisplayName} I couldn't find a playable song for “{Sanitize(query)}”.";
        }

        if (outcome.Failure == SongRequestFailure.RepeatBlocked)
        {
            return $"@{chatMessage.DisplayName} “{Sanitize(outcome.Title)}”: {outcome.FailureMessage}";
        }

        _userSongCooldowns[chatMessage.UserId] = now;
        var position = outcome.Position is null
            ? string.Empty
            : $" at position {outcome.Position}";

        return $"@{chatMessage.DisplayName} added “{Sanitize(outcome.Title)}” — {Sanitize(outcome.Artist)}{position}.";
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
