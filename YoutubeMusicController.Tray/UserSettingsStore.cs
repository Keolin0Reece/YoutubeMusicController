using System.Security.Cryptography;
using System.Text.Json;

namespace YoutubeMusicController.Tray;

internal sealed class UserSettingsStore
{
    private static readonly byte[] ApiKeyEntropy =
        System.Text.Encoding.UTF8.GetBytes(
            "YoutubeMusicController.LocalApiKey.v1");

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public UserSettingsStore()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "YoutubeMusicController");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        ApiKeyPath = Path.Combine(DataDirectory, "api-key.dat");
    }

    public string DataDirectory { get; }

    public string SettingsPath { get; }

    private string ApiKeyPath { get; }

    public bool HasSettings => File.Exists(SettingsPath);

    public ControllerUserSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new ControllerUserSettings();
        }

        return JsonSerializer.Deserialize<ControllerUserSettings>(
            File.ReadAllText(SettingsPath),
            _jsonOptions) ?? new ControllerUserSettings();
    }

    public void Save(ControllerUserSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(settings, _jsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public string GetOrCreateApiKey()
    {
        Directory.CreateDirectory(DataDirectory);

        if (File.Exists(ApiKeyPath))
        {
            var encrypted = File.ReadAllBytes(ApiKeyPath);
            var plaintext = ProtectedData.Unprotect(
                encrypted,
                ApiKeyEntropy,
                DataProtectionScope.CurrentUser);

            try
            {
                return Convert.ToBase64String(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectedData.Protect(
            key,
            ApiKeyEntropy,
            DataProtectionScope.CurrentUser);

        try
        {
            File.WriteAllBytes(ApiKeyPath, protectedKey);
            return Convert.ToBase64String(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }
}

internal sealed class ControllerUserSettings
{
    public int SchemaVersion { get; set; } = 1;

    public TwitchUserSettings Twitch { get; set; } = new();

    public DiscordUserSettings Discord { get; set; } = new();
}

internal sealed class TwitchUserSettings
{
    public string ClientId { get; set; } = string.Empty;

    public TwitchCommandSettings Commands { get; set; } = new();
}

internal sealed class TwitchCommandSettings
{
    public bool SongRequestsEnabled { get; set; } = true;
}

internal sealed class DiscordUserSettings
{
    public bool Enabled { get; set; }

    public ulong[] AllowedChannelIds { get; set; } = [];

    public DiscordCommandSettings Commands { get; set; } = new();
}

internal sealed class DiscordCommandSettings
{
    public string VideoRequesterUsername { get; set; } = string.Empty;
}
