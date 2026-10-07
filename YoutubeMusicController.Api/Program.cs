using Discord;
using Discord.WebSocket;
using YoutubeMusicController.Api.Models;
using YoutubeMusicController.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var applicationDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "YoutubeMusicController");
Directory.CreateDirectory(applicationDataDirectory);
builder.Configuration.AddJsonFile(
    Path.Combine(applicationDataDirectory, "settings.json"),
    optional: true,
    reloadOnChange: true);
builder.Logging.AddProvider(new RollingFileLoggerProvider(
    Path.Combine(applicationDataDirectory, "Logs")));

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
builder.Services.AddSingleton<SongRequestService>();
builder.Services.AddHostedService<SongPlaybackMonitor>();
builder.Services.AddSingleton<TwitchCommandProcessor>();
builder.Services.AddSingleton<TwitchTokenStore>();
builder.Services.AddSingleton<TwitchChatService>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<TwitchChatService>());
builder.Services.AddSingleton<DiscordTokenStore>();
builder.Services.AddSingleton<DiscordRequestCooldown>();
builder.Services.AddSingleton(_ => new DiscordSocketClient(
    new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds,
        LogGatewayIntentWarnings = true
    }));
builder.Services.AddSingleton(serviceProvider => new Discord.Interactions.InteractionService(
    serviceProvider.GetRequiredService<DiscordSocketClient>().Rest,
    new Discord.Interactions.InteractionServiceConfig
    {
        DefaultRunMode = Discord.Interactions.RunMode.Sync,
        LogLevel = LogSeverity.Info
    }));
builder.Services.AddSingleton<DiscordBotService>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<DiscordBotService>());

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5173",
                "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

var localApiKey = Environment.GetEnvironmentVariable("YMC_API_KEY");

if (!string.IsNullOrWhiteSpace(localApiKey))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api") &&
            !HttpMethods.IsOptions(context.Request.Method))
        {
            var suppliedKey = context.Request.Headers["X-Controller-Key"]
                .FirstOrDefault() ?? context.Request.Query["key"].FirstOrDefault();

            if (!FixedTimeEquals(localApiKey, suppliedKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Local controller authentication is required."
                });
                return;
            }
        }

        await next(context);
    });
}

app.UseCors("Frontend");
app.UseDefaultFiles();
app.UseStaticFiles();
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

app.MapGet("/api/discord/status", (DiscordBotService discordBotService) =>
    Results.Ok(discordBotService.GetStatus()));

app.MapPost("/api/discord/configure", async (
    DiscordConfigurationRequest request,
    DiscordTokenStore tokenStore,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    if (request.ApplicationId == 0 ||
        string.IsNullOrWhiteSpace(request.BotToken) ||
        request.BotToken.Trim().Length < 58)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.BotToken)] =
                ["A valid Discord Application ID and Bot Token are required."]
        });
    }

    var discordClient = httpClientFactory.CreateClient();
    using var identityRequest = new HttpRequestMessage(
        HttpMethod.Get,
        "https://discord.com/api/v10/users/@me");
    identityRequest.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bot",
            request.BotToken.Trim());
    using var identityResponse = await discordClient.SendAsync(
        identityRequest,
        cancellationToken);

    if (!identityResponse.IsSuccessStatusCode)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.BotToken)] =
                ["Discord rejected this Bot Token."]
        });
    }

    using var identity = await System.Text.Json.JsonDocument.ParseAsync(
        await identityResponse.Content.ReadAsStreamAsync(cancellationToken),
        cancellationToken: cancellationToken);
    var returnedId = identity.RootElement.GetProperty("id").GetString();

    if (!ulong.TryParse(returnedId, out var botId) ||
        botId != request.ApplicationId)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.ApplicationId)] =
                ["The Application ID does not match this Discord Bot Token."]
        });
    }

    await tokenStore.SaveAsync(
        new DiscordStoredState(
            request.ApplicationId,
            request.GuildId,
            request.BotToken.Trim()),
        cancellationToken);
    return Results.NoContent();
});

app.MapDelete("/api/discord/configure", async (
    DiscordTokenStore tokenStore) =>
{
    await tokenStore.DeleteAsync();
    return Results.NoContent();
});

app.MapPost("/api/application/shutdown", (
    IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.Accepted();
});

app.MapPost("/api/twitch/device-authorization", async (
    TwitchChatService twitchChatService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var verificationUri =
            await twitchChatService.BeginDeviceAuthorizationAsync(
                cancellationToken);
        return Results.Ok(new
        {
            verificationUri = verificationUri.AbsoluteUri
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
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

app.MapFallbackToFile("index.html");

await app.RunAsync();

static bool FixedTimeEquals(string expected, string? actual)
{
    if (actual is null)
    {
        return false;
    }

    var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
    var actualBytes = System.Text.Encoding.UTF8.GetBytes(actual);

    return expectedBytes.Length == actualBytes.Length &&
           System.Security.Cryptography.CryptographicOperations
               .FixedTimeEquals(expectedBytes, actualBytes);
}
