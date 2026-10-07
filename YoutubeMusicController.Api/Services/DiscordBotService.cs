using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using YoutubeMusicController.Api.Models;

namespace YoutubeMusicController.Api.Services;

public sealed class DiscordBotService(
    DiscordSocketClient client,
    InteractionService interactions,
    DiscordTokenStore tokenStore,
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<DiscordBotService> logger) : BackgroundService
{
    private readonly object _statusGate = new();
    private readonly HashSet<ulong> _allowedChannelIds = configuration
        .GetSection("Discord:AllowedChannelIds")
        .GetChildren()
        .Select(item => ulong.TryParse(item.Value, out var id) ? id : 0)
        .Where(id => id != 0)
        .ToHashSet();

    private DiscordStoredState? _state;
    private DiscordConnectionStatus _status = new(
        false,
        false,
        "Not configured",
        null,
        null);
    private int _commandsRegistered;

    public DiscordConnectionStatus GetStatus()
    {
        lock (_statusGate)
        {
            return _status;
        }
    }

    public bool IsInteractionAllowed(SocketInteractionContext context)
    {
        var state = _state;

        if (state?.GuildId is { } guildId && context.Guild?.Id != guildId)
        {
            return false;
        }

        return _allowedChannelIds.Count == 0 ||
               _allowedChannelIds.Contains(context.Channel.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Discord:Enabled", true))
        {
            SetStatus(new(false, false, "Disabled", null, null));
            return;
        }

        try
        {
            _state = await tokenStore.LoadAsync(stoppingToken);

            if (_state is null)
            {
                SetStatus(new(
                    false,
                    false,
                    "Not configured",
                    null,
                    "Run scripts\\Configure-Discord.ps1, then restart the controller."));
                return;
            }

            SetStatus(new(true, false, "Connecting", null, null));
            client.Log += LogAsync;
            interactions.Log += LogAsync;
            client.Ready += ReadyAsync;
            client.Disconnected += DisconnectedAsync;
            client.InteractionCreated += InteractionCreatedAsync;

            await interactions.AddModulesAsync(
                Assembly.GetExecutingAssembly(),
                serviceProvider);

            if (_state.BotToken.Length < 58)
            {
                throw new InvalidDataException(
                    "The saved value is not a valid Discord Bot Token. Run Configure Discord and use the token from the Developer Portal's Bot page.");
            }

            await client.LoginAsync(TokenType.Bot, _state.BotToken);
            await client.StartAsync();

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discord bot failed.");
            SetStatus(new(
                _state is not null,
                false,
                "Error",
                client.CurrentUser?.Username,
                ex.Message));
        }
        finally
        {
            if (client.LoginState != LoginState.LoggedOut)
            {
                await client.StopAsync();
                await client.LogoutAsync();
            }
        }
    }

    private async Task ReadyAsync()
    {
        if (_state is null)
        {
            return;
        }

        if (client.CurrentUser.Id != _state.ApplicationId)
        {
            throw new InvalidOperationException(
                "The configured Discord Application ID does not match the bot token.");
        }

        var shouldRegister = Interlocked.Exchange(
            ref _commandsRegistered,
            1) == 0;

        if (shouldRegister && _state.GuildId is { } guildId)
        {
            await interactions.RegisterCommandsToGuildAsync(
                guildId,
                deleteMissing: true);
        }
        else if (shouldRegister)
        {
            await interactions.RegisterCommandsGloballyAsync(
                deleteMissing: true);
        }

        SetStatus(new(
            true,
            true,
            "Connected",
            client.CurrentUser.Username,
            null));
        logger.LogInformation(
            "Discord connected as {Username}; commands registered {Scope}.",
            client.CurrentUser.Username,
            _state.GuildId is null ? "globally" : $"to guild {_state.GuildId}");
    }

    private Task DisconnectedAsync(Exception exception)
    {
        SetStatus(new(
            _state is not null,
            false,
            "Disconnected",
            client.CurrentUser?.Username,
            exception.Message));
        return Task.CompletedTask;
    }

    private async Task InteractionCreatedAsync(SocketInteraction interaction)
    {
        try
        {
            logger.LogInformation(
                "Received Discord interaction {InteractionId} ({InteractionType}).",
                interaction.Id,
                interaction.Type);
            using var scope = serviceProvider.CreateScope();
            var context = new SocketInteractionContext(client, interaction);
            var result = await interactions.ExecuteCommandAsync(
                context,
                scope.ServiceProvider);

            if (!result.IsSuccess)
            {
                logger.LogWarning(
                    "Discord interaction {Command} failed: {Reason}",
                    interaction.Type,
                    result.ErrorReason);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discord interaction failed.");

            if (!interaction.HasResponded)
            {
                await interaction.RespondAsync(
                    "I couldn't process that command right now.",
                    ephemeral: true);
            }
        }
    }

    private Task LogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug
        };

        logger.Log(
            level,
            message.Exception,
            "Discord {Source}: {Message}",
            message.Source,
            message.Message);
        return Task.CompletedTask;
    }

    private void SetStatus(DiscordConnectionStatus status)
    {
        lock (_statusGate)
        {
            _status = status;
        }
    }
}
