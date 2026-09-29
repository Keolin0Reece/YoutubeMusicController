using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

public sealed class SongRepeatCooldown
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RequestState> _requests =
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

    public bool HasTrackedRequests
    {
        get
        {
            lock (_gate)
            {
                RemoveExpiredCooldowns(DateTimeOffset.UtcNow);
                return _requests.Count > 0;
            }
        }
    }

    public bool TryReserve(
        string videoId,
        out Reservation reservation,
        out TimeSpan? remaining)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            RemoveExpiredCooldowns(now);

            if (_requests.TryGetValue(videoId, out var existing))
            {
                remaining = existing.CooldownStartedAt is null
                    ? null
                    : _cooldown - (now - existing.CooldownStartedAt.Value);
                reservation = default;
                return false;
            }

            _requests[videoId] = new RequestState(now);
            reservation = new Reservation(videoId, now);
            remaining = null;
            return true;
        }
    }

    public void ConfirmQueued(Reservation reservation, string? instanceId)
    {
        lock (_gate)
        {
            if (TryGetReservation(reservation, out var request))
            {
                request.InstanceId = instanceId;
                request.IsConfirmed = true;
            }
        }
    }

    public void Cancel(Reservation reservation)
    {
        lock (_gate)
        {
            if (TryGetReservation(reservation, out _))
            {
                _requests.Remove(reservation.VideoId);
            }
        }
    }

    public void Observe(QueueResponse queue)
    {
        var now = DateTimeOffset.UtcNow;
        var selectedItem = queue.Items.FirstOrDefault(item => item.IsSelected);

        lock (_gate)
        {
            RemoveExpiredCooldowns(now);

            foreach (var (videoId, request) in _requests.ToArray())
            {
                if (request.CooldownStartedAt is not null)
                {
                    continue;
                }

                if (!request.IsConfirmed)
                {
                    continue;
                }

                if (request.InstanceId is null)
                {
                    request.InstanceId = queue.Items
                        .LastOrDefault(item => item.VideoId == videoId)
                        ?.InstanceId;

                    if (request.InstanceId is null)
                    {
                        continue;
                    }
                }

                var requestedItemExists = queue.Items.Any(item =>
                    item.InstanceId == request.InstanceId);

                if (!request.HasStartedPlaying)
                {
                    if (selectedItem?.InstanceId == request.InstanceId)
                    {
                        request.HasStartedPlaying = true;
                    }
                    else if (!requestedItemExists)
                    {
                        _requests.Remove(videoId);
                    }

                    continue;
                }

                var anotherItemIsPlaying = selectedItem is not null &&
                    selectedItem.InstanceId != request.InstanceId;
                var playbackEndedWithNoSelection = selectedItem is null &&
                    !requestedItemExists;

                if (anotherItemIsPlaying || playbackEndedWithNoSelection)
                {
                    request.CooldownStartedAt = now;
                }
            }
        }
    }

    public string GetBlockedMessage(TimeSpan? remaining) =>
        remaining is null
            ? $"This song can be queued again {FormatDuration(_cooldown)} after it finishes playing."
            : $"This song can be queued again in {FormatRemaining(remaining.Value)}.";

    public static string FormatRemaining(TimeSpan remaining)
    {
        var totalSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;

        return minutes > 0
            ? $"{minutes}m {seconds}s"
            : $"{seconds}s";
    }

    private bool TryGetReservation(
        Reservation reservation,
        out RequestState request) =>
        _requests.TryGetValue(reservation.VideoId, out request!) &&
        request.RequestedAt == reservation.ReservedAt;

    private void RemoveExpiredCooldowns(DateTimeOffset now)
    {
        foreach (var (videoId, request) in _requests.ToArray())
        {
            if (request.CooldownStartedAt is not null &&
                now - request.CooldownStartedAt.Value >= _cooldown)
            {
                _requests.Remove(videoId);
            }
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1 && duration.TotalMinutes % 1 == 0)
        {
            var minutes = (int)duration.TotalMinutes;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        return FormatRemaining(duration);
    }

    private sealed class RequestState(DateTimeOffset requestedAt)
    {
        public DateTimeOffset RequestedAt { get; } = requestedAt;
        public bool IsConfirmed { get; set; }
        public string? InstanceId { get; set; }
        public bool HasStartedPlaying { get; set; }
        public DateTimeOffset? CooldownStartedAt { get; set; }
    }

    public readonly record struct Reservation(
        string VideoId,
        DateTimeOffset ReservedAt);
}
