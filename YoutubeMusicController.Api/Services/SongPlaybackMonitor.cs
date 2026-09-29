namespace YoutubeMusicController.Api.Services;

public sealed class SongPlaybackMonitor(
    IServiceScopeFactory scopeFactory,
    SongRepeatCooldown songRepeatCooldown,
    ILogger<SongPlaybackMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (songRepeatCooldown.HasTrackedRequests)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var youtubeMusicService = scope.ServiceProvider
                        .GetRequiredService<YoutubeMusicService>();
                    var queue = await youtubeMusicService.GetQueueAsync(
                        stoppingToken);

                    songRepeatCooldown.Observe(queue);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        ex,
                        "Unable to observe YouTube Music playback for song repeat cooldowns.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
