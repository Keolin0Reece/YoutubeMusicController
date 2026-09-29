namespace YoutubeMusicController.Api.Services;

public sealed class SongRepeatCooldown
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _queuedAt =
        new(StringComparer.Ordinal);
    private readonly TimeSpan _cooldown;

    public SongRepeatCooldown(IConfiguration configuration)
    {
        _cooldown = TimeSpan.FromMinutes(Math.Max(
            0,
            configuration.GetValue(
                "QueuePolicy:RepeatCooldownMinutes",
                10)));
    }

    public bool TryReserve(
        string videoId,
        out Reservation reservation,
        out TimeSpan remaining)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (_queuedAt.TryGetValue(videoId, out var lastQueuedAt))
            {
                remaining = _cooldown - (now - lastQueuedAt);

                if (remaining > TimeSpan.Zero)
                {
                    reservation = default;
                    return false;
                }
            }

            _queuedAt[videoId] = now;
            reservation = new Reservation(videoId, now);
            remaining = TimeSpan.Zero;
            return true;
        }
    }

    public void Cancel(Reservation reservation)
    {
        lock (_gate)
        {
            if (_queuedAt.TryGetValue(
                    reservation.VideoId,
                    out var queuedAt) &&
                queuedAt == reservation.ReservedAt)
            {
                _queuedAt.Remove(reservation.VideoId);
            }
        }
    }

    public static string FormatRemaining(TimeSpan remaining)
    {
        var totalSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;

        return minutes > 0
            ? $"{minutes}m {seconds}s"
            : $"{seconds}s";
    }

    public readonly record struct Reservation(
        string VideoId,
        DateTimeOffset ReservedAt);
}
