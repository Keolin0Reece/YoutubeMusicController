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

app.MapPost("/api/ytmd/queue", async (
    AddToQueueRequest request,
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

    try
    {
        await youtubeMusicService.AddToQueueAsync(
            request.VideoId.Trim(),
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
