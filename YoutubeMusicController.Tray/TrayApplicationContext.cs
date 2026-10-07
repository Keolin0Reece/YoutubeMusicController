using System.Diagnostics;

namespace YoutubeMusicController.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly UserSettingsStore _settingsStore = new();
    private readonly ControllerProcess _controller;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _connectTwitchItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly System.Windows.Forms.Timer _statusTimer;

    private bool _operationInProgress;
    private bool _controllerRunning;

    public TrayApplicationContext()
    {
        _controller = new ControllerProcess(
            _settingsStore.GetOrCreateApiKey());

        _statusItem = new ToolStripMenuItem("Status: Starting…")
        {
            Enabled = false
        };
        _startItem = new ToolStripMenuItem(
            "Start controller",
            null,
            async (_, _) => await StartControllerAsync());
        _restartItem = new ToolStripMenuItem(
            "Restart controller",
            null,
            async (_, _) => await RestartControllerAsync());
        _stopItem = new ToolStripMenuItem(
            "Stop controller",
            null,
            async (_, _) => await StopControllerAsync());
        _connectTwitchItem = new ToolStripMenuItem(
            "Connect Twitch",
            null,
            async (_, _) => await OpenTwitchAuthorizationAsync());
        _settingsItem = new ToolStripMenuItem(
            "Settings…",
            null,
            async (_, _) => await ConfigureSettingsAsync());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            new ToolStripSeparator(),
            _startItem,
            _restartItem,
            _stopItem,
            new ToolStripSeparator(),
            _connectTwitchItem,
            _settingsItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem(
                "Exit",
                null,
                async (_, _) => await StopAndExitAsync())
        ]);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Application,
            Text = "YouTube Music Controller",
            Visible = true
        };
        _notifyIcon.DoubleClick += async (_, _) =>
        {
            if (_controllerRunning)
            {
                OpenDashboard();
            }
            else
            {
                await StartControllerAsync();
            }
        };

        _statusTimer = new System.Windows.Forms.Timer
        {
            Interval = 3000,
            Enabled = true
        };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (!_settingsStore.HasSettings)
        {
            await ConfigureSettingsAsync(firstRun: true);
        }

        await StartControllerAsync(showNotification: false);
    }

    private async Task StartControllerAsync(bool showNotification = true)
    {
        if (_operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        UpdateMenu("Status: Starting…");

        try
        {
            await _controller.StartAsync();
            _controllerRunning = true;

            if (showNotification)
            {
                ShowNotification("Controller started.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            _controllerRunning = false;
            MessageBox.Show(
                ex.Message,
                "YouTube Music Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _operationInProgress = false;
            await RefreshStatusAsync();
        }
    }

    private async Task StopControllerAsync(bool showNotification = true)
    {
        if (_operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        UpdateMenu("Status: Stopping…");

        try
        {
            await _controller.StopAsync();
            _controllerRunning = false;

            if (showNotification)
            {
                ShowNotification("Controller stopped.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "YouTube Music Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _operationInProgress = false;
            await RefreshStatusAsync();
        }
    }

    private async Task RestartControllerAsync()
    {
        await StopControllerAsync(showNotification: false);
        await StartControllerAsync(showNotification: false);
        ShowNotification("Controller restarted.", ToolTipIcon.Info);
    }

    private async Task ConfigureSettingsAsync(bool firstRun = false)
    {
        var settings = _settingsStore.Load();
        using var form = new SettingsForm(settings);

        if (form.ShowDialog() != DialogResult.OK)
        {
            if (firstRun)
            {
                ShowNotification(
                    "Open Settings when you are ready to connect Twitch or Discord.",
                    ToolTipIcon.Info);
            }

            return;
        }

        if (!form.TryGetDiscordCredentials(
                out var applicationId,
                out var guildId,
                out var botToken))
        {
            return;
        }

        settings.Twitch.ClientId = form.TwitchClientId;
        settings.Discord.Enabled = form.DiscordEnabled;
        settings.Discord.Commands.VideoRequesterUsername =
            form.VideoRequesterUsername;
        _settingsStore.Save(settings);

        await StopControllerAsync(showNotification: false);
        await StartControllerAsync(showNotification: false);

        if (form.HasDiscordCredentials)
        {
            try
            {
                await _controller.ConfigureDiscordAsync(
                    applicationId,
                    guildId,
                    botToken);
                await RestartControllerAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Settings were saved, but Discord credentials could not be stored: {ex.Message}",
                    "YouTube Music Controller",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        else if (!form.DiscordEnabled)
        {
            await _controller.DisconnectDiscordAsync();
            await RestartControllerAsync();
        }

        ShowNotification("Settings saved.", ToolTipIcon.Info);
    }

    private async Task RefreshStatusAsync()
    {
        if (_operationInProgress)
        {
            return;
        }

        var health = await _controller.GetHealthAsync();
        _controllerRunning = health.IsRunning;
        UpdateMenu(health switch
        {
            { IsRunning: false } => "Status: Stopped",
            { IsYoutubeMusicConnected: true } =>
                "Status: Running — YouTube Music connected",
            _ => "Status: Running — waiting for YouTube Music"
        });
    }

    private void UpdateMenu(string statusText)
    {
        _statusItem.Text = statusText;
        _notifyIcon.Text = statusText.Length <= 127
            ? statusText
            : statusText[..127];
        _startItem.Enabled = !_operationInProgress && !_controllerRunning;
        _restartItem.Enabled = !_operationInProgress && _controllerRunning;
        _stopItem.Enabled = !_operationInProgress && _controllerRunning;
        _connectTwitchItem.Enabled =
            !_operationInProgress && _controllerRunning;
        _settingsItem.Enabled = !_operationInProgress;
        _notifyIcon.Icon = _controllerRunning
            ? SystemIcons.Information
            : SystemIcons.Application;
    }

    private async Task OpenTwitchAuthorizationAsync()
    {
        try
        {
            var authorizationUri =
                await _controller.BeginTwitchAuthorizationAsync();
            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Connect Twitch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OpenDashboard()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"http://127.0.0.1:5161/#key={Uri.EscapeDataString(_controller.ApiKey)}",
            UseShellExecute = true
        });
    }

    private async Task StopAndExitAsync()
    {
        await StopControllerAsync(showNotification: false);
        ExitThread();
    }

    private void ShowNotification(string message, ToolTipIcon icon)
    {
        _notifyIcon.BalloonTipTitle = "YouTube Music Controller";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(3000);
    }

    protected override void ExitThreadCore()
    {
        _statusTimer.Stop();
        _notifyIcon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Dispose();
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _controller.Dispose();
        }

        base.Dispose(disposing);
    }
}
