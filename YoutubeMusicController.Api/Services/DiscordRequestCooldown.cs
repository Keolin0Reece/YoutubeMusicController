using System.Collections.Concurrent;

namespace YoutubeMusicController.Api.Services;

public sealed class DiscordRequestCooldown
{
    private readonly ConcurrentDictionary<ulong, DateTimeOffset>
        _userRequests = new();
    private readonly object _gate = new();
    private readonly TimeSpan _userCooldown;
    private readonly TimeSpan _globalCooldown;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public DiscordRequestCooldown(IConfiguration configuration)
    {
        _userCooldown = TimeSpan.FromSeconds(Math.Max(
            0,
            configuration.GetValue("Discord:Commands:UserCooldownSeconds", 30)));
        _globalCooldown = TimeSpan.FromSeconds(Math.Max(
            0,
            configuration.GetValue("Discord:Commands:GlobalCooldownSeconds", 2)));
    }

    public bool TryBegin(
        ulong userId,
        bool bypass,
        out string? blockedMessage)
    {
        blockedMessage = null;

        if (bypass)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (_userRequests.TryGetValue(userId, out var userLastRequest))
            {
                var remaining = _userCooldown - (now - userLastRequest);

                if (remaining > TimeSpan.Zero)
                {
                    blockedMessage = $"Wait {Math.Ceiling(remaining.TotalSeconds)}s before requesting another song.";
                    return false;
                }
            }

            var globalRemaining = _globalCooldown - (now - _lastRequestAt);

            if (globalRemaining > TimeSpan.Zero)
            {
                blockedMessage = $"Song requests are cooling down for {Math.Ceiling(globalRemaining.TotalSeconds)}s.";
                return false;
            }

            _lastRequestAt = now;
            return true;
        }
    }

    public void Confirm(ulong userId) =>
        _userRequests[userId] = DateTimeOffset.UtcNow;
}
