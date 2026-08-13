using System.Security.Cryptography;
using System.Text;
using RobotCompetitionBooth.Web.Models;

namespace RobotCompetitionBooth.Web.Services;

public sealed class WifiCredentialStore
{
    private const byte StorageFormatVersion = 1;
    private const int StorageHeaderLength = 3;
    private const int MaximumEncryptedFileLength = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("RobotCompetitionBooth.Wifi.v1");

    private readonly object storageLock = new();
    private readonly string credentialFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RobotCompetitionBooth",
        "wifi-credentials.dat");

    public WifiConfigurationStatus GetStatus()
    {
        lock (storageLock)
        {
            var stored = ReadStoredCredentials(includePassword: false);
            return stored is null
                ? new(false, null)
                : new(true, stored.Value.NetworkName);
        }
    }

    public WifiCredentials? GetCredentials()
    {
        lock (storageLock)
        {
            var stored = ReadStoredCredentials(includePassword: true);
            return stored is null || stored.Value.Password is null
                ? null
                : new WifiCredentials(stored.Value.NetworkName, stored.Value.Password);
        }
    }

    public void Save(string networkName, string password)
    {
        if (!TryValidate(networkName, password, out var validationMessage))
        {
            throw new ArgumentException(validationMessage);
        }

        var networkNameBytes = StrictUtf8.GetBytes(networkName);
        var passwordBytes = StrictUtf8.GetBytes(password);
        var plaintext = new byte[StorageHeaderLength + networkNameBytes.Length + passwordBytes.Length];
        byte[]? encrypted = null;

        try
        {
            plaintext[0] = StorageFormatVersion;
            plaintext[1] = checked((byte)networkNameBytes.Length);
            plaintext[2] = checked((byte)passwordBytes.Length);
            networkNameBytes.CopyTo(plaintext, StorageHeaderLength);
            passwordBytes.CopyTo(plaintext, StorageHeaderLength + networkNameBytes.Length);
            encrypted = ProtectedData.Protect(plaintext, OptionalEntropy, DataProtectionScope.CurrentUser);

            lock (storageLock)
            {
                WriteAtomically(encrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(networkNameBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    public bool Delete()
    {
        lock (storageLock)
        {
            if (!File.Exists(credentialFilePath))
            {
                return false;
            }

            File.Delete(credentialFilePath);
            return true;
        }
    }

    public static bool TryValidate(string? networkName, string? password, out string? validationMessage)
    {
        var suppliedNetworkName = networkName ?? string.Empty;
        if (suppliedNetworkName.Length == 0)
        {
            validationMessage = "Enter the Wi-Fi network name (SSID).";
            return false;
        }

        int networkNameByteCount;
        int passwordByteCount;
        try
        {
            networkNameByteCount = StrictUtf8.GetByteCount(suppliedNetworkName);
            passwordByteCount = password is null ? 0 : StrictUtf8.GetByteCount(password);
        }
        catch (EncoderFallbackException)
        {
            validationMessage = "The Wi-Fi network name and password must contain valid Unicode text.";
            return false;
        }

        if (suppliedNetworkName.Contains('\0') || networkNameByteCount > 32)
        {
            validationMessage = "The Wi-Fi network name must be no more than 32 UTF-8 bytes.";
            return false;
        }

        if (password is null || password.Contains('\0'))
        {
            validationMessage = "Enter a valid Wi-Fi password.";
            return false;
        }

        if (passwordByteCount == 0)
        {
            validationMessage = null;
            return true;
        }

        var isPassphrase = passwordByteCount is >= 8 and <= 63;
        var isRawPsk = password.Length == 64 && password.All(char.IsAsciiHexDigit);
        if (!isPassphrase && !isRawPsk)
        {
            validationMessage = "The Wi-Fi password must be 8–63 UTF-8 bytes, or a 64-digit hexadecimal key.";
            return false;
        }

        validationMessage = null;
        return true;
    }

    private (string NetworkName, string? Password)? ReadStoredCredentials(bool includePassword)
    {
        if (!File.Exists(credentialFilePath))
        {
            return null;
        }

        var fileInfo = new FileInfo(credentialFilePath);
        if (fileInfo.Length is <= 0 or > MaximumEncryptedFileLength)
        {
            throw new InvalidDataException("The saved Wi-Fi credential file has an invalid size.");
        }

        var encrypted = File.ReadAllBytes(credentialFilePath);
        byte[]? plaintext = null;

        try
        {
            plaintext = ProtectedData.Unprotect(encrypted, OptionalEntropy, DataProtectionScope.CurrentUser);
            if (plaintext.Length < StorageHeaderLength || plaintext[0] != StorageFormatVersion)
            {
                throw new InvalidDataException("The saved Wi-Fi credential format is not supported.");
            }

            var networkNameLength = plaintext[1];
            var passwordLength = plaintext[2];
            if (networkNameLength is < 1 or > 32 ||
                passwordLength > 64 ||
                plaintext.Length != StorageHeaderLength + networkNameLength + passwordLength)
            {
                throw new InvalidDataException("The saved Wi-Fi credential data is invalid.");
            }

            var networkName = StrictUtf8.GetString(
                plaintext,
                StorageHeaderLength,
                networkNameLength);
            var password = includePassword
                ? StrictUtf8.GetString(
                    plaintext,
                    StorageHeaderLength + networkNameLength,
                    passwordLength)
                : null;

            if (includePassword && !TryValidate(networkName, password, out _))
            {
                throw new InvalidDataException("The saved Wi-Fi credentials are invalid.");
            }

            return (networkName, password);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "The saved Wi-Fi credentials cannot be decrypted for the current Windows user.",
                exception);
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

    private void WriteAtomically(byte[] encrypted)
    {
        var directoryPath = Path.GetDirectoryName(credentialFilePath)
            ?? throw new InvalidOperationException("The local credential directory could not be resolved.");
        Directory.CreateDirectory(directoryPath);

        var temporaryPath = Path.Combine(directoryPath, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, encrypted);
            File.Move(temporaryPath, credentialFilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
