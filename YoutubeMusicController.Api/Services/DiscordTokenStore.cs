using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace YoutubeMusicController.Api.Services;

public sealed class DiscordTokenStore
{
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes(
            "YoutubeMusicController.DiscordAuthorization.v1"));

    private readonly string _filePath;

    public DiscordTokenStore(IConfiguration configuration)
    {
        _filePath = configuration["Discord:TokenStorePath"] ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "YoutubeMusicController",
            "discord-auth.dat");
    }

    public async Task<DiscordStoredState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(
            _filePath,
            cancellationToken);
        byte[]? plaintext = null;

        try
        {
            plaintext = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.LocalMachine);

            return JsonSerializer.Deserialize<DiscordStoredState>(plaintext)
                ?? throw new InvalidDataException(
                    "The Discord authorization store was empty.");
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
        DiscordStoredState state,
        CancellationToken cancellationToken = default)
    {
        var directoryPath = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException(
                "The Discord token store path has no directory.");

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
                DataProtectionScope.LocalMachine);
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

    private static void RestrictDirectoryAccess(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);

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
        security.SetAccessRuleProtection(true, false);

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
        var identities = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        var currentUser = WindowsIdentity.GetCurrent().User;

        if (currentUser is not null)
        {
            identities.Add(currentUser);
        }

        return identities;
    }
}

public sealed record DiscordStoredState(
    ulong ApplicationId,
    ulong? GuildId,
    string BotToken);
