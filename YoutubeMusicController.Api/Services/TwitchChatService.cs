using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

public sealed class TwitchChatService : BackgroundService
{
    private const string EventSubWebSocketUrl =
        "wss://eventsub.wss.twitch.tv/ws";
    private const int RecentMessageLimit = 20;

    private static readonly string[] RequiredScopes =
    [
        "user:read:chat",
        "user:write:chat"
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TwitchCommandProcessor _commandProcessor;
    private readonly ILogger<TwitchChatService> _logger;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly string _redirectUri;
    private readonly string _frontendUri;
    private readonly object _gate = new();
    private readonly List<TwitchChatMessage> _recentMessages = [];

    private string? _oauthState;
    private DateTimeOffset _oauthStateExpiresAt;
    private TwitchAuthorization? _authorization;
    private CancellationTokenSource _authorizationChanged = new();
    private string _state = "Disconnected";
    private string? _connectionError;
    private bool _connected;

    public TwitchChatService(
        IHttpClientFactory httpClientFactory,
        TwitchCommandProcessor commandProcessor,
        IConfiguration configuration,
        ILogger<TwitchChatService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _commandProcessor = commandProcessor;
        _logger = logger;
        _clientId = configuration["Twitch:ClientId"];
        _clientSecret = configuration["Twitch:ClientSecret"];
        _redirectUri = configuration["Twitch:RedirectUri"]
            ?? "http://localhost:5161/api/twitch/callback";
        _frontendUri = configuration["Twitch:FrontendUri"]
            ?? "http://localhost:5173";
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_clientId) &&
        !string.IsNullOrWhiteSpace(_clientSecret);

    public string FrontendUri => _frontendUri;

    public TwitchConnectionStatus GetStatus()
    {
        lock (_gate)
        {
            return new TwitchConnectionStatus(
                IsConfigured,
                _authorization is not null,
                _connected,
                IsConfigured ? _state : "Not configured",
                _authorization?.DisplayName,
                _connectionError);
        }
    }

    public IReadOnlyList<TwitchChatMessage> GetRecentMessages()
    {
        lock (_gate)
        {
            return _recentMessages.ToList();
        }
    }

    public Uri CreateAuthorizationUri()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Twitch client credentials are not configured.");
        }

        var stateBytes = RandomNumberGenerator.GetBytes(32);
        var state = Convert.ToHexString(stateBytes);

        lock (_gate)
        {
            _oauthState = state;
            _oauthStateExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        }

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _clientId,
            ["redirect_uri"] = _redirectUri,
            ["scope"] = string.Join(' ', RequiredScopes),
            ["state"] = state
        };

        return new Uri(
            $"https://id.twitch.tv/oauth2/authorize{QueryString.Create(query)}");
    }

    public async Task CompleteAuthorizationAsync(
        string code,
        string state,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Twitch client credentials are not configured.");
        }

        if (!ConsumeOAuthState(state))
        {
            throw new InvalidOperationException(
                "The Twitch authorization state is invalid or expired.");
        }

        var client = _httpClientFactory.CreateClient();
        using var tokenResponse = await client.PostAsync(
            "https://id.twitch.tv/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId!,
                ["client_secret"] = _clientSecret!,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = _redirectUri
            }),
            cancellationToken);

        tokenResponse.EnsureSuccessStatusCode();

        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Twitch returned an empty token response.");

        var identity = await ValidateTokenAsync(
            token.AccessToken,
            cancellationToken);

        var missingScopes = RequiredScopes
            .Except(identity.Scopes, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingScopes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Twitch did not grant the required scopes: {string.Join(", ", missingScopes)}.");
        }

        SetAuthorization(new TwitchAuthorization(
            token.AccessToken,
            token.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            identity.UserId,
            identity.Login,
            identity.Login));
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            _authorization = null;
            _connected = false;
            _state = "Disconnected";
            _connectionError = null;
            RotateAuthorizationChangedToken();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TwitchAuthorization? authorization;
            CancellationToken authorizationToken;

            lock (_gate)
            {
                authorization = _authorization;
                authorizationToken = _authorizationChanged.Token;
            }

            if (authorization is null)
            {
                await WaitForAuthorizationChangeAsync(
                    authorizationToken,
                    stoppingToken);
                continue;
            }

            using var connectionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    authorizationToken,
                    stoppingToken);

            try
            {
                SetConnectionState("Connecting", false, null);
                await RunEventSubConnectionAsync(
                    authorization,
                    connectionCancellation.Token);
            }
            catch (OperationCanceledException)
                when (connectionCancellation.IsCancellationRequested)
            {
                // Authorization changed, the user disconnected, or the app stopped.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Twitch chat connection failed.");
                SetConnectionState(
                    "Reconnecting",
                    false,
                    "Unable to maintain the Twitch chat connection.");

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        connectionCancellation.Token);
                }
                catch (OperationCanceledException)
                    when (connectionCancellation.IsCancellationRequested)
                {
                    // The next loop observes the new authorization state.
                }
            }
        }
    }

    private async Task RunEventSubConnectionAsync(
        TwitchAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var connectionUri = new Uri(EventSubWebSocketUrl);
        var shouldCreateSubscription = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var webSocket = new ClientWebSocket();
            webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            await webSocket.ConnectAsync(connectionUri, cancellationToken);

            var reconnectUri = await ReceiveEventSubMessagesAsync(
                webSocket,
                authorization,
                shouldCreateSubscription,
                cancellationToken);

            if (reconnectUri is null)
            {
                return;
            }

            connectionUri = reconnectUri;
            shouldCreateSubscription = false;
        }
    }

    private async Task<Uri?> ReceiveEventSubMessagesAsync(
        ClientWebSocket webSocket,
        TwitchAuthorization authorization,
        bool shouldCreateSubscription,
        CancellationToken cancellationToken)
    {
        while (webSocket.State == WebSocketState.Open &&
               !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveTextMessageAsync(
                webSocket,
                cancellationToken);

            if (message is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            var messageType = root
                .GetProperty("metadata")
                .GetProperty("message_type")
                .GetString();

            switch (messageType)
            {
                case "session_welcome":
                    {
                        var session = root
                            .GetProperty("payload")
                            .GetProperty("session");
                        var sessionId = session.GetProperty("id").GetString()
                            ?? throw new InvalidOperationException(
                                "Twitch omitted the EventSub session ID.");

                        if (shouldCreateSubscription)
                        {
                            await CreateChatSubscriptionAsync(
                                sessionId,
                                authorization,
                                cancellationToken);
                        }

                        SetConnectionState("Connected", true, null);
                        break;
                    }
                case "notification":
                    await AddChatMessageAsync(
                        root,
                        authorization,
                        cancellationToken);
                    break;
                case "session_reconnect":
                    {
                        var reconnectUrl = root
                            .GetProperty("payload")
                            .GetProperty("session")
                            .GetProperty("reconnect_url")
                            .GetString();

                        return string.IsNullOrWhiteSpace(reconnectUrl)
                            ? null
                            : new Uri(reconnectUrl);
                    }
                case "revocation":
                    throw new InvalidOperationException(
                        "Twitch revoked the chat subscription.");
            }
        }

        return null;
    }

    private async Task CreateChatSubscriptionAsync(
        string sessionId,
        TwitchAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.twitch.tv/helix/eventsub/subscriptions");

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            authorization.AccessToken);
        request.Headers.Add("Client-Id", _clientId);
        request.Content = JsonContent.Create(new
        {
            type = "channel.chat.message",
            version = "1",
            condition = new
            {
                broadcaster_user_id = authorization.UserId,
                user_id = authorization.UserId
            },
            transport = new
            {
                method = "websocket",
                session_id = sessionId
            }
        });

        using var response = await client.SendAsync(
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task AddChatMessageAsync(
        JsonElement root,
        TwitchAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var payload = root.GetProperty("payload");
        var subscriptionType = payload
            .GetProperty("subscription")
            .GetProperty("type")
            .GetString();

        if (subscriptionType != "channel.chat.message")
        {
            return;
        }

        var eventData = payload.GetProperty("event");
        var chatMessage = new TwitchChatMessage(
            eventData.GetProperty("message_id").GetString() ?? string.Empty,
            eventData.GetProperty("chatter_user_id").GetString() ?? string.Empty,
            eventData.GetProperty("chatter_user_login").GetString() ?? string.Empty,
            eventData.GetProperty("chatter_user_name").GetString() ?? string.Empty,
            eventData.GetProperty("message").GetProperty("text").GetString() ?? string.Empty,
            HasBadge(eventData, "broadcaster"),
            HasBadge(eventData, "moderator"),
            DateTimeOffset.UtcNow);

        var isNewMessage = false;

        lock (_gate)
        {
            if (!_recentMessages.Any(item => item.Id == chatMessage.Id))
            {
                _recentMessages.Insert(0, chatMessage);
                isNewMessage = true;

                if (_recentMessages.Count > RecentMessageLimit)
                {
                    _recentMessages.RemoveRange(
                        RecentMessageLimit,
                        _recentMessages.Count - RecentMessageLimit);
                }
            }
        }

        if (!isNewMessage)
        {
            return;
        }

        var response = await _commandProcessor.ProcessAsync(
            chatMessage,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(response))
        {
            try
            {
                await SendChatMessageAsync(
                    response,
                    authorization,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unable to send the Twitch chat reply.");
            }
        }
    }

    private async Task SendChatMessageAsync(
        string message,
        TwitchAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.twitch.tv/helix/chat/messages");

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            authorization.AccessToken);
        request.Headers.Add("Client-Id", _clientId);
        request.Content = JsonContent.Create(new
        {
            broadcaster_id = authorization.UserId,
            sender_id = authorization.UserId,
            message = message.Length <= 450 ? message : message[..450]
        });

        using var response = await client.SendAsync(
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<SendChatMessageResponse>(
                cancellationToken: cancellationToken);
        var sendResult = result?.Data.FirstOrDefault();

        if (sendResult?.IsSent != true)
        {
            throw new InvalidOperationException(
                sendResult?.DropReason?.Message ??
                "Twitch did not send the chat message.");
        }
    }

    private static bool HasBadge(JsonElement eventData, string badgeName)
    {
        if (!eventData.TryGetProperty("badges", out var badges) ||
            badges.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return badges.EnumerateArray().Any(badge =>
            badge.TryGetProperty("set_id", out var setId) &&
            setId.GetString() == badgeName);
    }

    private async Task<ValidatedToken> ValidateTokenAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "OAuth",
            accessToken);

        using var response = await client.SendAsync(
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ValidatedToken>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Twitch returned an empty validation response.");
    }

    private bool ConsumeOAuthState(string state)
    {
        lock (_gate)
        {
            var isValid =
                _oauthState is not null &&
                DateTimeOffset.UtcNow <= _oauthStateExpiresAt &&
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(_oauthState),
                    Encoding.UTF8.GetBytes(state));

            _oauthState = null;
            _oauthStateExpiresAt = default;
            return isValid;
        }
    }

    private void SetAuthorization(TwitchAuthorization authorization)
    {
        lock (_gate)
        {
            _authorization = authorization;
            _connected = false;
            _state = "Connecting";
            _connectionError = null;
            RotateAuthorizationChangedToken();
        }
    }

    private void RotateAuthorizationChangedToken()
    {
        var previous = _authorizationChanged;
        _authorizationChanged = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
    }

    private void SetConnectionState(
        string state,
        bool connected,
        string? error)
    {
        lock (_gate)
        {
            _state = state;
            _connected = connected;
            _connectionError = error;
        }
    }

    private static async Task WaitForAuthorizationChangeAsync(
        CancellationToken authorizationToken,
        CancellationToken stoppingToken)
    {
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                authorizationToken,
                stoppingToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linkedCancellation.Token);
        }
        catch (OperationCanceledException)
            when (linkedCancellation.IsCancellationRequested)
        {
            // Expected when authorization changes or the app shuts down.
        }
    }

    private static async Task<string?> ReceiveTextMessageAsync(
        ClientWebSocket webSocket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await webSocket.ReceiveAsync(
                buffer,
                cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private sealed record TwitchAuthorization(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAt,
        string UserId,
        string Login,
        string DisplayName);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record ValidatedToken(
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes);

    private sealed record SendChatMessageResponse(
        [property: JsonPropertyName("data")]
        IReadOnlyList<SendChatMessageResult> Data);

    private sealed record SendChatMessageResult(
        [property: JsonPropertyName("is_sent")] bool IsSent,
        [property: JsonPropertyName("drop_reason")]
        SendChatMessageDropReason? DropReason);

    private sealed record SendChatMessageDropReason(
        [property: JsonPropertyName("message")] string Message);
}
