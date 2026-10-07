using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security;
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
    private readonly TwitchTokenStore _tokenStore;
    private readonly ILogger<TwitchChatService> _logger;
    private string? _clientId;
    private readonly object _gate = new();
    private readonly List<TwitchChatMessage> _recentMessages = [];

    private CancellationTokenSource? _deviceAuthorizationCancellation;
    private TwitchAuthorization? _authorization;
    private CancellationTokenSource _authorizationChanged = new();
    private string _state = "Disconnected";
    private string? _connectionError;
    private bool _connected;

    public TwitchChatService(
        IHttpClientFactory httpClientFactory,
        TwitchCommandProcessor commandProcessor,
        TwitchTokenStore tokenStore,
        IConfiguration configuration,
        ILogger<TwitchChatService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _commandProcessor = commandProcessor;
        _tokenStore = tokenStore;
        _logger = logger;
        _clientId = configuration["Twitch:ClientId"];
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_clientId);

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

    public async Task<Uri> BeginDeviceAuthorizationAsync(
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "A Twitch Client ID is required. Open Settings and configure the public Twitch application first.");
        }

        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            "https://id.twitch.tv/oauth2/device",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId!,
                ["scopes"] = string.Join(' ', RequiredScopes)
            }),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var device = await response.Content
            .ReadFromJsonAsync<DeviceAuthorizationResponse>(
                cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Twitch returned an empty device authorization response.");

        var authorizationCancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(device.ExpiresIn));

        lock (_gate)
        {
            _deviceAuthorizationCancellation?.Cancel();
            _deviceAuthorizationCancellation?.Dispose();
            _deviceAuthorizationCancellation = authorizationCancellation;
            _state = $"Waiting for Twitch code {device.UserCode}";
            _connectionError = null;
        }

        _ = PollDeviceAuthorizationAsync(
            device,
            authorizationCancellation);

        return new Uri(device.VerificationUri, UriKind.Absolute);
    }

    private async Task PollDeviceAuthorizationAsync(
        DeviceAuthorizationResponse device,
        CancellationTokenSource authorizationCancellation)
    {
        try
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, device.Interval));

            while (!authorizationCancellation.IsCancellationRequested)
            {
                await Task.Delay(interval, authorizationCancellation.Token);
                var token = await TryExchangeDeviceCodeAsync(
                    device.DeviceCode,
                    authorizationCancellation.Token);

                if (token is null)
                {
                    continue;
                }

                var authorization = await CreateAuthorizationAsync(
                    token,
                    fallbackRefreshToken: null,
                    authorizationCancellation.Token);
                await SaveAuthorizationAsync(
                    authorization,
                    authorizationCancellation.Token);
                SetAuthorization(authorization);
                return;
            }
        }
        catch (OperationCanceledException)
            when (authorizationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twitch device authorization failed.");
            SetConnectionState(
                "Authorization failed",
                false,
                "Twitch authorization could not be completed. Try connecting again.");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(
                        _deviceAuthorizationCancellation,
                        authorizationCancellation))
                {
                    _deviceAuthorizationCancellation = null;
                }
            }

            authorizationCancellation.Dispose();
        }
    }

    private async Task<TokenResponse?> TryExchangeDeviceCodeAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            "https://id.twitch.tv/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId!,
                ["scopes"] = string.Join(' ', RequiredScopes),
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }),
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TokenResponse>(
                cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException(
                    "Twitch returned an empty token response.");
        }

        var error = await response.Content
            .ReadFromJsonAsync<TwitchErrorResponse>(
                cancellationToken: cancellationToken);

        if (response.StatusCode == HttpStatusCode.BadRequest &&
            string.Equals(
                error?.Message,
                "authorization_pending",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return null;
    }

    public async Task DisconnectAsync()
    {
        lock (_gate)
        {
            _deviceAuthorizationCancellation?.Cancel();
            _authorization = null;
            _connected = false;
            _state = "Disconnected";
            _connectionError = null;
            RotateAuthorizationChangedToken();
        }

        await SaveCredentialsWithoutAuthorizationAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RestoreAuthorizationAsync(stoppingToken);

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

            var refreshAfter = authorization.ExpiresAt -
                DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);

            if (refreshAfter <= TimeSpan.Zero)
            {
                var refreshed = await TryRefreshAuthorizationAsync(
                    authorization,
                    stoppingToken);

                if (!refreshed)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(30),
                        stoppingToken);
                }

                continue;
            }

            using var refreshCancellation = new CancellationTokenSource(
                refreshAfter);

            using var connectionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    authorizationToken,
                    stoppingToken,
                    refreshCancellation.Token);

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
                // Authorization changed, refresh is due, the user disconnected,
                // or the app stopped.
            }
            catch (TwitchAuthorizationRevokedException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Twitch revoked the chat subscription. Browser authorization is required again.");
                await ClearAuthorizationAsync(
                    "Authorization required",
                    "Twitch revoked chat access. Connect Twitch again.");
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                var refreshed = await TryRefreshAuthorizationAsync(
                    authorization,
                    stoppingToken);

                if (!refreshed)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(30),
                        stoppingToken);
                }
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

    private async Task RestoreAuthorizationAsync(
        CancellationToken cancellationToken)
    {
        TwitchStoredState? storedState;

        try
        {
            storedState = await _tokenStore.LoadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException or
                                   SecurityException or
                                   CryptographicException or
                                   JsonException)
        {
            _logger.LogError(
                ex,
                "Unable to read the encrypted Twitch authorization store at {Path}.",
                _tokenStore.FilePath);
            SetConnectionState(
                "Authorization unavailable",
                false,
                "The encrypted Twitch authorization could not be read. Connect Twitch again.");
            return;
        }

        if (storedState is null)
        {
            return;
        }

        _clientId ??= storedState.ClientId;
        if (!IsConfigured || storedState.Authorization is null)
        {
            return;
        }

        var stored = storedState.Authorization;

        var storedAuthorization = new TwitchAuthorization(
            stored.AccessToken,
            stored.RefreshToken,
            stored.ExpiresAt,
            stored.UserId,
            stored.Login,
            stored.DisplayName);

        try
        {
            var identity = await ValidateTokenAsync(
                stored.AccessToken,
                cancellationToken);
            ValidateIdentity(identity);

            var authorization = storedAuthorization with
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                    identity.ExpiresIn),
                UserId = identity.UserId,
                Login = identity.Login,
                DisplayName = identity.Login
            };

            await SaveAuthorizationAsync(authorization, cancellationToken);
            SetAuthorization(authorization);
            _logger.LogInformation(
                "Restored Twitch authorization for {Login}.",
                authorization.Login);
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            await TryRefreshAuthorizationAsync(
                storedAuthorization,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "The saved Twitch authorization is no longer valid. Browser authorization is required again.");
            await ClearAuthorizationAsync(
                "Authorization required",
                "The saved Twitch authorization is no longer valid. Connect Twitch again.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Twitch authorization could not be validated during startup; it will be retried.");
            SetAuthorization(storedAuthorization);
        }
    }

    private async Task<bool> TryRefreshAuthorizationAsync(
        TwitchAuthorization authorization,
        CancellationToken cancellationToken)
    {
        SetConnectionState("Refreshing authorization", false, null);

        try
        {
            var token = await RequestRefreshTokenAsync(
                authorization.RefreshToken,
                cancellationToken);
            var refreshedAuthorization = await CreateAuthorizationAsync(
                token,
                authorization.RefreshToken,
                cancellationToken);

            await SaveAuthorizationAsync(
                refreshedAuthorization,
                cancellationToken);
            SetAuthorization(refreshedAuthorization);
            _logger.LogInformation(
                "Refreshed Twitch authorization for {Login}.",
                refreshedAuthorization.Login);
            return true;
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is HttpStatusCode.BadRequest or
                  HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning(
                ex,
                "Twitch rejected the saved refresh token. Browser authorization is required again.");
            await ClearAuthorizationAsync(
                "Authorization required",
                "Twitch authorization expired or was revoked. Connect Twitch again.");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "The saved Twitch authorization cannot be refreshed. Browser authorization is required again.");
            await ClearAuthorizationAsync(
                "Authorization required",
                "The saved Twitch authorization cannot be refreshed. Connect Twitch again.");
            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to refresh Twitch authorization; retrying shortly.");
            SetConnectionState(
                "Waiting to refresh authorization",
                false,
                "Unable to refresh Twitch authorization. Retrying automatically.");
            return false;
        }
    }

    private async Task<TokenResponse> RequestRefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException(
                "The saved Twitch authorization has no refresh token.");
        }

        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            "https://id.twitch.tv/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId!,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TokenResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Twitch returned an empty refresh response.");
    }

    private async Task<TwitchAuthorization> CreateAuthorizationAsync(
        TokenResponse token,
        string? fallbackRefreshToken,
        CancellationToken cancellationToken)
    {
        var identity = await ValidateTokenAsync(
            token.AccessToken,
            cancellationToken);
        ValidateIdentity(identity);
        var refreshToken = string.IsNullOrWhiteSpace(token.RefreshToken)
            ? fallbackRefreshToken
            : token.RefreshToken;

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException(
                "Twitch did not return a refresh token.");
        }

        return new TwitchAuthorization(
            token.AccessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(
                identity.ExpiresIn > 0
                    ? identity.ExpiresIn
                    : token.ExpiresIn),
            identity.UserId,
            identity.Login,
            identity.Login);
    }

    private void ValidateIdentity(ValidatedToken identity)
    {
        if (!string.Equals(
                identity.ClientId,
                _clientId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Twitch token belongs to a different application.");
        }

        var missingScopes = RequiredScopes
            .Except(identity.Scopes, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingScopes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Twitch did not grant the required scopes: {string.Join(", ", missingScopes)}.");
        }
    }

    private Task SaveAuthorizationAsync(
        TwitchAuthorization authorization,
        CancellationToken cancellationToken) =>
        _tokenStore.SaveAsync(
            new TwitchStoredState(
                _clientId!,
                new TwitchStoredAuthorization(
                    authorization.AccessToken,
                    authorization.RefreshToken,
                    authorization.ExpiresAt,
                    authorization.UserId,
                    authorization.Login,
                    authorization.DisplayName)),
            cancellationToken);

    private Task SaveCredentialsWithoutAuthorizationAsync() =>
        IsConfigured
            ? _tokenStore.SaveAsync(new TwitchStoredState(
                _clientId!,
                Authorization: null))
            : _tokenStore.DeleteAsync();

    private async Task ClearAuthorizationAsync(
        string state,
        string? error)
    {
        lock (_gate)
        {
            _authorization = null;
            _connected = false;
            _state = state;
            _connectionError = error;
            RotateAuthorizationChangedToken();
        }

        await SaveCredentialsWithoutAuthorizationAsync();
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
                    throw new TwitchAuthorizationRevokedException(
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
            catch (HttpRequestException ex)
                when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw;
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
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record DeviceAuthorizationResponse(
        [property: JsonPropertyName("device_code")] string DeviceCode,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

    private sealed record TwitchErrorResponse(
        [property: JsonPropertyName("message")] string? Message);

    private sealed record ValidatedToken(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
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

    private sealed class TwitchAuthorizationRevokedException(string message)
        : Exception(message);
}
