using YoutubeMusicController.Api.Models;
using YoutubeMusicController.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Ytmd", client =>
{
    var baseUrl = builder.Configuration["YoutubeMusic:BaseUrl"]
        ?? throw new InvalidOperationException(
            "Configuration value 'YoutubeMusic:BaseUrl' is required.");

    client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddScoped<YoutubeMusicService>();
builder.Services.AddSingleton<YoutubeMusicRealtimeProxy>();
builder.Services.AddSingleton<SongRepeatCooldown>();
builder.Services.AddHostedService<SongPlaybackMonitor>();
builder.Services.AddSingleton<TwitchCommandProcessor>();
builder.Services.AddSingleton<TwitchTokenStore>();
builder.Services.AddSingleton<TwitchChatService>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<TwitchChatService>());

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("Frontend");
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

app.Map("/api/ytmd/realtime", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("A WebSocket request is required.");
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var realtimeProxy = context.RequestServices
        .GetRequiredService<YoutubeMusicRealtimeProxy>();

    await realtimeProxy.RelayAsync(
        webSocket,
        context.RequestAborted);
});

app.MapGet("/api/ytmd/status", async (
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    var connected = await youtubeMusicService.IsConnectedAsync(
        cancellationToken);

    return Results.Ok(new
    {
        connected
    });
});

app.MapPost("/api/ytmd/search", async (
    SearchRequest request,
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Query) &&
        string.IsNullOrWhiteSpace(request.Continuation))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Query)] =
                ["A query or continuation token is required."]
        });
    }

    try
    {
        var response = await youtubeMusicService.SearchAsync(
            request,
            cancellationToken);

        return Results.Ok(response);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/ytmd/now-playing", async (
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var song = await youtubeMusicService.GetNowPlayingAsync(
            cancellationToken);

        return Results.Ok(song);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/twitch/status", (TwitchChatService twitchChatService) =>
    Results.Ok(twitchChatService.GetStatus()));

app.MapGet("/api/twitch/messages", (TwitchChatService twitchChatService) =>
    Results.Ok(twitchChatService.GetRecentMessages()));

app.MapGet("/api/twitch/connect", (TwitchChatService twitchChatService) =>
{
    try
    {
        return Results.Redirect(
            twitchChatService.CreateAuthorizationUri().AbsoluteUri);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/twitch/callback", async (
    string? code,
    string? state,
    string? error,
    TwitchChatService twitchChatService,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    if (!string.IsNullOrWhiteSpace(error))
    {
        return TwitchCallbackPage(
            "Twitch connection cancelled",
            "Twitch did not grant access. You can close this window and try again when ready.",
            success: false);
    }

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
    {
        return TwitchCallbackPage(
            "Invalid Twitch response",
            "The authorization response was incomplete. Close this window and start the connection again.",
            success: false);
    }

    try
    {
        await twitchChatService.CompleteAuthorizationAsync(
            code,
            state,
            cancellationToken);

        return TwitchCallbackPage(
            "Twitch connected",
            "Authorization was encrypted and saved locally. The controller will reconnect automatically after future restarts.",
            success: true);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        loggerFactory
            .CreateLogger("TwitchAuthorization")
            .LogError(ex, "Unable to complete Twitch authorization.");

        return TwitchCallbackPage(
            "Twitch connection failed",
            "The controller could not complete authorization. Check its logs, then close this window and try again.",
            success: false);
    }
});

app.MapPost("/api/twitch/disconnect", async (
    TwitchChatService twitchChatService) =>
{
    await twitchChatService.DisconnectAsync();
    return Results.NoContent();
});

app.MapPost("/api/ytmd/queue", async (
    AddToQueueRequest request,
    SongRepeatCooldown songRepeatCooldown,
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.VideoId))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.VideoId)] = ["A video ID is required."]
        });
    }

    if (!songRepeatCooldown.TryReserve(
            request.VideoId.Trim(),
            out var reservation,
            out var repeatRemaining))
    {
        return Results.Problem(
            detail: songRepeatCooldown.GetBlockedMessage(repeatRemaining),
            statusCode: StatusCodes.Status409Conflict);
    }

    try
    {
        await youtubeMusicService.AddToQueueAsync(
            request.VideoId.Trim(),
            cancellationToken);
    }
    catch (HttpRequestException ex)
    {
        songRepeatCooldown.Cancel(reservation);
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch
    {
        songRepeatCooldown.Cancel(reservation);
        throw;
    }

    try
    {
        var queue = await youtubeMusicService.GetQueueAsync(cancellationToken);
        var queuedItem = queue.Items.LastOrDefault(item =>
            item.VideoId == request.VideoId.Trim());
        songRepeatCooldown.ConfirmQueued(
            reservation,
            queuedItem?.InstanceId);
    }
    catch (HttpRequestException)
    {
        songRepeatCooldown.ConfirmQueued(reservation, null);
    }
    catch
    {
        songRepeatCooldown.ConfirmQueued(reservation, null);
        throw;
    }

    return Results.NoContent();
});

app.MapGet("/api/ytmd/queue", async (
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var queue = await youtubeMusicService.GetQueueAsync(cancellationToken);
        return Results.Ok(queue);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPatch("/api/ytmd/queue/{index:int:min(0)}", async (
    int index,
    MoveQueueItemRequest request,
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    if (request.ToIndex < 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.ToIndex)] = ["The destination index cannot be negative."]
        });
    }

    try
    {
        await youtubeMusicService.MoveQueueItemAsync(
            index,
            request.ToIndex,
            cancellationToken);

        return Results.NoContent();
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapDelete("/api/ytmd/queue/{index:int:min(0)}", async (
    int index,
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await youtubeMusicService.RemoveQueueItemAsync(
            index,
            cancellationToken);

        return Results.NoContent();
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapDelete("/api/ytmd/queue", async (
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await youtubeMusicService.ClearQueueAsync(cancellationToken);
        return Results.NoContent();
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/ytmd/play-pause", async (
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await youtubeMusicService.TogglePlayPauseAsync(
            cancellationToken);

        return Results.NoContent();
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/ytmd/next", async (
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await youtubeMusicService.NextAsync(cancellationToken);

        return Results.NoContent();
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/ytmd/previous", async (
    YoutubeMusicService youtubeMusicService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await youtubeMusicService.PreviousAsync(cancellationToken);

        return Results.NoContent();
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

static IResult TwitchCallbackPage(
    string title,
    string message,
    bool success)
{
    var accent = success ? "#4ade80" : "#fb7185";
    var html = $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{title}}</title>
          <style>
            :root { color-scheme: dark; font-family: system-ui, sans-serif; }
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #111827; color: #f9fafb; }
            main { width: min(34rem, calc(100% - 3rem)); padding: 2rem; border: 1px solid #374151; border-radius: 1rem; background: #1f2937; box-shadow: 0 1rem 3rem #0006; }
            h1 { margin-top: 0; color: {{accent}}; }
            p { line-height: 1.6; color: #d1d5db; }
            button { margin-top: 1rem; padding: .7rem 1rem; border: 0; border-radius: .6rem; font: inherit; cursor: pointer; }
          </style>
        </head>
        <body>
          <main>
            <h1>{{title}}</h1>
            <p>{{message}}</p>
            <button type="button" onclick="window.close()">Close window</button>
          </main>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html; charset=utf-8");
}
