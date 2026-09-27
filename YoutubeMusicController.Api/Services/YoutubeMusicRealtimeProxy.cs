using System.Net.WebSockets;

namespace YoutubeMusicController.Api.Services;

public sealed class YoutubeMusicRealtimeProxy
{
    private readonly Uri _webSocketUri;

    public YoutubeMusicRealtimeProxy(IConfiguration configuration)
    {
        var baseUrl = configuration["YoutubeMusic:BaseUrl"]
            ?? throw new InvalidOperationException(
                "Configuration value 'YoutubeMusic:BaseUrl' is required.");
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        var uriBuilder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/api/v1/ws"
        };
        var accessToken = configuration["YoutubeMusic:AccessToken"];

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            uriBuilder.Query = $"token={Uri.EscapeDataString(accessToken)}";
        }

        _webSocketUri = uriBuilder.Uri;
    }

    public async Task RelayAsync(
        WebSocket downstream,
        CancellationToken cancellationToken)
    {
        using var upstream = new ClientWebSocket();

        try
        {
            await upstream.ConnectAsync(_webSocketUri, cancellationToken);

            using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var relayTask = RelayMessagesAsync(
                upstream,
                downstream,
                relayCancellation.Token);
            var watchTask = WatchDownstreamAsync(
                downstream,
                relayCancellation.Token);

            await Task.WhenAny(relayTask, watchTask);
            await relayCancellation.CancelAsync();

            try
            {
                await Task.WhenAll(relayTask, watchTask);
            }
            catch (OperationCanceledException)
            {
                // Expected when either side closes the connection.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The browser disconnected or the request was aborted.
        }
        catch (WebSocketException)
        {
            await CloseIfOpenAsync(
                downstream,
                WebSocketCloseStatus.EndpointUnavailable,
                "YouTube Music realtime connection unavailable.",
                CancellationToken.None);
        }
        finally
        {
            await CloseIfOpenAsync(
                downstream,
                WebSocketCloseStatus.NormalClosure,
                "Realtime connection closed.",
                CancellationToken.None);
        }
    }

    private static async Task RelayMessagesAsync(
        WebSocket upstream,
        WebSocket downstream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        while (!cancellationToken.IsCancellationRequested &&
               upstream.State == WebSocketState.Open &&
               downstream.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await upstream.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                await message.WriteAsync(
                    buffer.AsMemory(0, result.Count),
                    cancellationToken);
            }
            while (!result.EndOfMessage);

            await downstream.SendAsync(
                new ArraySegment<byte>(message.ToArray()),
                result.MessageType,
                true,
                cancellationToken);
        }
    }

    private static async Task WatchDownstreamAsync(
        WebSocket downstream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];

        while (!cancellationToken.IsCancellationRequested &&
               downstream.State == WebSocketState.Open)
        {
            var result = await downstream.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    private static async Task CloseIfOpenAsync(
        WebSocket socket,
        WebSocketCloseStatus closeStatus,
        string description,
        CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(
                    closeStatus,
                    description,
                    cancellationToken);
            }
            catch (WebSocketException)
            {
                // The peer has already disconnected.
            }
        }
    }
}
