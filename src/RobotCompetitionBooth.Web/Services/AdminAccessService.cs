using System.Security.Cryptography;
using System.Text;

namespace RobotCompetitionBooth.Web.Services;

public sealed class AdminAccessService
{
    private const string PasswordConfigurationKey = "Admin:Password";

    private readonly byte[] configuredPasswordHash;

    public AdminAccessService(IConfiguration configuration)
    {
        var configuredPassword = configuration[PasswordConfigurationKey] ?? string.Empty;
        IsConfigured = !string.IsNullOrEmpty(configuredPassword);
        configuredPasswordHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredPassword));
    }

    public bool IsConfigured { get; }

    public bool IsUnlocked { get; private set; }

    public bool TryUnlock(string? password)
    {
        if (!IsConfigured)
        {
            return false;
        }

        var suppliedPasswordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
        try
        {
            var suppliedPasswordHash = SHA256.HashData(suppliedPasswordBytes);
            IsUnlocked = CryptographicOperations.FixedTimeEquals(
                configuredPasswordHash,
                suppliedPasswordHash);
            return IsUnlocked;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedPasswordBytes);
        }
    }

    public void Lock() => IsUnlocked = false;
}
