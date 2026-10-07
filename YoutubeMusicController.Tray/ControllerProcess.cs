using System.Diagnostics;
using System.Net.Http.Json;

namespace YoutubeMusicController.Tray;

internal sealed class ControllerProcess : IDisposable
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:5161");

    private readonly HttpClient _client;
    private Process? _process;

    public ControllerProcess(string apiKey)
    {
        ApiKey = apiKey;
        _client = new HttpClient
        {
            BaseAddress = BaseAddress,
            Timeout = TimeSpan.FromSeconds(5)
        };
        _client.DefaultRequestHeaders.Add("X-Controller-Key", apiKey);
    }

    public string ApiKey { get; }

    public bool OwnsProcess => _process is { HasExited: false };

    public async Task<bool> IsRunningAsync()
    {
        try
        {
            using var response = await _client.GetAsync("/api/ytmd/status");
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<ControllerHealth> GetHealthAsync()
    {
        try
        {
            using var response = await _client.GetAsync("/api/ytmd/status");

            if (!response.IsSuccessStatusCode)
            {
                return new ControllerHealth(false, false);
            }

            var status = await response.Content
                .ReadFromJsonAsync<YoutubeMusicStatus>();
            return new ControllerHealth(true, status?.Connected == true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ControllerHealth(false, false);
        }
    }

    public async Task StartAsync()
    {
        if (await IsRunningAsync())
        {
            return;
        }

        var executable = FindControllerExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--urls http://127.0.0.1:5161",
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["YMC_API_KEY"] = ApiKey;

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows could not start the controller process.");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The controller stopped during startup with exit code {_process.ExitCode}.");
            }

            if (await IsRunningAsync())
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException("The controller did not start within 20 seconds.");
    }

    public async Task StopAsync()
    {
        if (!await IsRunningAsync())
        {
            if (OwnsProcess)
            {
                _process!.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            return;
        }

        try
        {
            using var response = await _client.PostAsync(
                "/api/application/shutdown",
                content: null);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException) when (OwnsProcess)
        {
            _process!.Kill(entireProcessTree: true);
        }

        if (OwnsProcess)
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(10));

            try
            {
                await _process!.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _process!.Kill(entireProcessTree: true);
            }
        }
    }

    public async Task ConfigureDiscordAsync(
        ulong applicationId,
        ulong? guildId,
        string botToken)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/discord/configure",
            new
            {
                applicationId,
                guildId,
                botToken
            });
        response.EnsureSuccessStatusCode();
    }

    public async Task DisconnectDiscordAsync()
    {
        using var response = await _client.DeleteAsync(
            "/api/discord/configure");
        response.EnsureSuccessStatusCode();
    }

    public async Task<Uri> BeginTwitchAuthorizationAsync()
    {
        using var response = await _client.PostAsync(
            "/api/twitch/device-authorization",
            content: null);
        response.EnsureSuccessStatusCode();
        var result = await response.Content
            .ReadFromJsonAsync<TwitchDeviceAuthorizationResult>()
            ?? throw new InvalidOperationException(
                "The controller returned an empty Twitch authorization response.");
        return new Uri(result.VerificationUri, UriKind.Absolute);
    }

    private static string FindControllerExecutable()
    {
        var installedCandidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "Controller",
            "YoutubeMusicController.Api.exe"));

        if (File.Exists(installedCandidate))
        {
            return installedCandidate;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "YoutubeMusicController.slnx")))
            {
                var developmentCandidate = Path.Combine(
                    directory.FullName,
                    "YoutubeMusicController.Api",
                    "bin",
                    "Release",
                    "net10.0-windows",
                    "YoutubeMusicController.Api.exe");

                if (File.Exists(developmentCandidate))
                {
                    return developmentCandidate;
                }

                break;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "The controller executable is missing. Reinstall YouTube Music Controller.",
            installedCandidate);
    }

    public void Dispose()
    {
        _client.Dispose();
        _process?.Dispose();
    }

    private sealed record TwitchDeviceAuthorizationResult(
        string VerificationUri);

    private sealed record YoutubeMusicStatus(bool Connected);
}

internal readonly record struct ControllerHealth(
    bool IsRunning,
    bool IsYoutubeMusicConnected);
