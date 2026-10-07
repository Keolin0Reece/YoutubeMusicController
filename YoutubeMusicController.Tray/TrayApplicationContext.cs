using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace YoutubeMusicController.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName = "YoutubeMusicController";
    private const string TwitchAuthorizationUrl =
        "http://localhost:5161/api/twitch/connect";

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _killItem;
    private readonly ToolStripMenuItem _authorizeItem;
    private readonly ToolStripMenuItem _configureDiscordItem;
    private readonly System.Windows.Forms.Timer _statusTimer;

    private bool _operationInProgress;
    private bool _serviceInstalled;
    private ServiceControllerStatus? _serviceStatus;

    public TrayApplicationContext()
    {
        _statusItem = new ToolStripMenuItem("Status: Checking…")
        {
            Enabled = false
        };
        _startItem = new ToolStripMenuItem(
            "Start controller",
            null,
            async (_, _) => await StartServiceAsync());
        _stopItem = new ToolStripMenuItem(
            "Stop controller",
            null,
            async (_, _) => await StopServiceAsync());
        _killItem = new ToolStripMenuItem(
            "Kill controller",
            null,
            async (_, _) => await KillServiceAsync());
        _authorizeItem = new ToolStripMenuItem(
            "Connect Twitch",
            null,
            (_, _) => OpenTwitchAuthorization());
        _configureDiscordItem = new ToolStripMenuItem(
            "Configure Discord",
            null,
            (_, _) => ConfigureDiscord());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            new ToolStripSeparator(),
            _startItem,
            _stopItem,
            _killItem,
            new ToolStripSeparator(),
            _authorizeItem,
            _configureDiscordItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem(
                "Exit tray",
                null,
                (_, _) => ExitThread()),
            new ToolStripMenuItem(
                "Stop controller and exit",
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
            RefreshStatus();

            if (_serviceStatus == ServiceControllerStatus.Stopped)
            {
                await StartServiceAsync();
            }
            else if (_serviceStatus == ServiceControllerStatus.Running)
            {
                await StopServiceAsync();
            }
        };

        _statusTimer = new System.Windows.Forms.Timer
        {
            Interval = 2000,
            Enabled = true
        };
        _statusTimer.Tick += (_, _) => RefreshStatus();

        RefreshStatus();
    }

    private async Task StartServiceAsync()
    {
        await RunServiceCommandAsync(
            "Starting controller…",
            "sc.exe",
            $"start {ServiceName}",
            ServiceControllerStatus.Running,
            "Controller started.");
    }

    private async Task StopServiceAsync()
    {
        await RunServiceCommandAsync(
            "Stopping controller…",
            "sc.exe",
            $"stop {ServiceName}",
            ServiceControllerStatus.Stopped,
            "Controller stopped.");
    }

    private async Task KillServiceAsync()
    {
        var confirmation = MessageBox.Show(
            "This immediately terminates the controller without graceful cleanup. " +
            "Use it only when Stop controller is not working. Continue?",
            "Kill YouTube Music Controller",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        await RunServiceCommandAsync(
            "Killing controller…",
            "taskkill.exe",
            $"/F /T /FI \"SERVICES eq {ServiceName}\" /IM *",
            ServiceControllerStatus.Stopped,
            "Controller was forcibly terminated.");
    }

    private async Task StopAndExitAsync()
    {
        RefreshStatus();

        if (_serviceStatus is not null and not ServiceControllerStatus.Stopped)
        {
            var stopped = await RunServiceCommandAsync(
                "Stopping controller…",
                "sc.exe",
                $"stop {ServiceName}",
                ServiceControllerStatus.Stopped,
                "Controller stopped.");

            if (!stopped)
            {
                return;
            }
        }

        ExitThread();
    }

    private async Task<bool> RunServiceCommandAsync(
        string pendingText,
        string fileName,
        string arguments,
        ServiceControllerStatus expectedStatus,
        string successMessage)
    {
        if (_operationInProgress)
        {
            return false;
        }

        _operationInProgress = true;
        UpdateMenu(pendingText);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException(
                $"Windows could not start {fileName}.");

            await process.WaitForExitAsync();
            var reachedExpectedStatus = await WaitForStatusAsync(
                expectedStatus,
                TimeSpan.FromSeconds(15));

            if (!reachedExpectedStatus)
            {
                throw new InvalidOperationException(
                    $"The service did not reach the {expectedStatus} state.");
            }

            ShowNotification(successMessage, ToolTipIcon.Info);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowNotification(
                "The Administrator permission prompt was cancelled.",
                ToolTipIcon.Warning);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "YouTube Music Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _operationInProgress = false;
            RefreshStatus();
        }
    }

    private static async Task<bool> WaitForStatusAsync(
        ServiceControllerStatus expectedStatus,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var controller = new ServiceController(ServiceName);
                controller.Refresh();

                if (controller.Status == expectedStatus)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private void RefreshStatus()
    {
        if (_operationInProgress)
        {
            return;
        }

        try
        {
            using var controller = new ServiceController(ServiceName);
            controller.Refresh();
            _serviceInstalled = true;
            _serviceStatus = controller.Status;
            UpdateMenu($"Status: {FormatStatus(controller.Status)}");
        }
        catch (InvalidOperationException)
        {
            _serviceInstalled = false;
            _serviceStatus = null;
            UpdateMenu("Status: Service not installed");
        }
    }

    private void UpdateMenu(string statusText)
    {
        _statusItem.Text = statusText;
        _notifyIcon.Text = statusText.Length <= 127
            ? statusText
            : statusText[..127];

        var stopped = _serviceStatus == ServiceControllerStatus.Stopped;
        var running = _serviceStatus == ServiceControllerStatus.Running;

        _startItem.Enabled = !_operationInProgress &&
            _serviceInstalled &&
            stopped;
        _stopItem.Enabled = !_operationInProgress &&
            _serviceInstalled &&
            running;
        _killItem.Enabled = !_operationInProgress &&
            _serviceInstalled &&
            !stopped;
        _authorizeItem.Enabled = !_operationInProgress && running;
        _configureDiscordItem.Enabled = !_operationInProgress;

        _notifyIcon.Icon = _serviceStatus switch
        {
            ServiceControllerStatus.Running => SystemIcons.Information,
            ServiceControllerStatus.StartPending or
            ServiceControllerStatus.StopPending => SystemIcons.Warning,
            null when !_serviceInstalled => SystemIcons.Error,
            _ => SystemIcons.Application
        };
    }

    private static string FormatStatus(ServiceControllerStatus status) =>
        status switch
        {
            ServiceControllerStatus.StartPending => "Starting",
            ServiceControllerStatus.StopPending => "Stopping",
            ServiceControllerStatus.PausePending => "Pausing",
            ServiceControllerStatus.ContinuePending => "Resuming",
            _ => status.ToString()
        };

    private static void OpenTwitchAuthorization()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = TwitchAuthorizationUrl,
            UseShellExecute = true
        });
    }

    private static void ConfigureDiscord()
    {
        var scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "Configure-Discord.ps1"));

        if (!File.Exists(scriptPath))
        {
            MessageBox.Show(
                $"Discord setup script was not found at {scriptPath}.",
                "YouTube Music Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas"
        });
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
        }

        base.Dispose(disposing);
    }
}
