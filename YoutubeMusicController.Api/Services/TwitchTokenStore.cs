using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace YoutubeMusicController.Api.Services;

public sealed class TwitchTokenStore
{
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes(
            "YoutubeMusicController.TwitchAuthorization.v1"));

    private readonly string _filePath;
    private readonly string _legacyFilePath;

    public TwitchTokenStore(IConfiguration configuration)
    {
        _filePath = configuration["Twitch:TokenStorePath"] ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "YoutubeMusicController",
            "twitch-auth.dat");
        _legacyFilePath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "YoutubeMusicController",
            "twitch-auth.dat");
    }

    public string FilePath => _filePath;

    public async Task<TwitchStoredState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath) && File.Exists(_legacyFilePath))
        {
            var migrated = await ReadAsync(
                _legacyFilePath,
                DataProtectionScope.LocalMachine,
                cancellationToken);
            await SaveAsync(migrated, cancellationToken);
            return migrated;
        }

        if (!File.Exists(_filePath))
        {
            return null;
        }

        return await ReadAsync(
            _filePath,
            DataProtectionScope.CurrentUser,
            cancellationToken);
    }

    private static async Task<TwitchStoredState> ReadAsync(
        string path,
        DataProtectionScope scope,
        CancellationToken cancellationToken)
    {
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[]? plaintext = null;

        try
        {
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, scope);

            return JsonSerializer.Deserialize<TwitchStoredState>(
                plaintext)
                ?? throw new InvalidDataException(
                    "The Twitch authorization store was empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public async Task SaveAsync(
        TwitchStoredState state,
        CancellationToken cancellationToken = default)
    {
        var directoryPath = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException(
                "The Twitch token store path has no directory.");

        Directory.CreateDirectory(directoryPath);
        RestrictDirectoryAccess(directoryPath);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
        byte[]? encrypted = null;
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            encrypted = ProtectedData.Protect(
                plaintext,
                Entropy,
                DataProtectionScope.CurrentUser);

            await File.WriteAllBytesAsync(
                temporaryPath,
                encrypted,
                cancellationToken);
            RestrictFileAccess(temporaryPath);
            File.Move(temporaryPath, _filePath, true);
            RestrictFileAccess(_filePath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);

            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task DeleteAsync()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }

        return Task.CompletedTask;
    }

    private static void RestrictDirectoryAccess(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var identity in GetAllowedIdentities())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void RestrictFileAccess(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var identity in GetAllowedIdentities())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
    }

    private static IReadOnlyCollection<SecurityIdentifier> GetAllowedIdentities()
    {
        var identities = new HashSet<SecurityIdentifier>();
        var currentUser = WindowsIdentity.GetCurrent().User;

        if (currentUser is not null)
        {
            identities.Add(currentUser);
        }

        return identities;
    }
}

public sealed record TwitchStoredState(
    string ClientId,
    TwitchStoredAuthorization? Authorization);

public sealed record TwitchStoredAuthorization(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string UserId,
    string Login,
    string DisplayName);
