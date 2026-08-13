using System.Security.Cryptography;
using System.Text;

namespace RobotCompetitionBooth.Web.Services;

public sealed class MqttBrokerAccessService : IDisposable
{
    public const string DeviceUsername = "robobooth";

    private const int TokenLength = 32;
    private const int MaximumEncryptedFileLength = 1024;

    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("RobotCompetitionBooth.MqttBroker.v1");

    private readonly object tokenLock = new();
    private readonly string tokenFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RobotCompetitionBooth",
        "mqtt-broker-token.dat");
    private byte[]? token;
    private bool disposed;

    public (string Username, string Password) GetCredentials()
    {
        lock (tokenLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureTokenLoaded();
            return (DeviceUsername, Convert.ToHexString(token!));
        }
    }

    public bool Validate(string? username, byte[]? rawPassword)
    {
        if (!string.Equals(username, DeviceUsername, StringComparison.Ordinal) ||
            rawPassword is null ||
            rawPassword.Length != TokenLength * 2)
        {
            return false;
        }

        byte[] suppliedToken;
        try
        {
            suppliedToken = Convert.FromHexString(Encoding.ASCII.GetString(rawPassword));
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            lock (tokenLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                EnsureTokenLoaded();
                return suppliedToken.Length == TokenLength &&
                    CryptographicOperations.FixedTimeEquals(suppliedToken, token!);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedToken);
        }
    }

    private void EnsureTokenLoaded()
    {
        if (token is not null)
        {
            return;
        }

        if (File.Exists(tokenFilePath))
        {
            var fileInfo = new FileInfo(tokenFilePath);
            if (fileInfo.Length is <= 0 or > MaximumEncryptedFileLength)
            {
                throw new InvalidDataException("The saved MQTT broker credential file has an invalid size.");
            }

            var encrypted = File.ReadAllBytes(tokenFilePath);
            try
            {
                token = ProtectedData.Unprotect(encrypted, OptionalEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "The MQTT broker credentials cannot be decrypted for the current Windows user.",
                    exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            if (token.Length == TokenLength)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(token);
            token = null;
            throw new InvalidDataException("The saved MQTT broker credential is invalid.");
        }

        var generatedToken = RandomNumberGenerator.GetBytes(TokenLength);
        byte[]? encryptedToken = null;
        try
        {
            encryptedToken = ProtectedData.Protect(
                generatedToken,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            WriteAtomically(encryptedToken);
            token = generatedToken;
            generatedToken = [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generatedToken);
            if (encryptedToken is not null)
            {
                CryptographicOperations.ZeroMemory(encryptedToken);
            }
        }
    }

    private void WriteAtomically(byte[] encrypted)
    {
        var directoryPath = Path.GetDirectoryName(tokenFilePath)
            ?? throw new InvalidOperationException("The local MQTT credential directory could not be resolved.");
        Directory.CreateDirectory(directoryPath);

        var temporaryPath = Path.Combine(directoryPath, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, encrypted);
            File.Move(temporaryPath, tokenFilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void Dispose()
    {
        lock (tokenLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (token is not null)
            {
                CryptographicOperations.ZeroMemory(token);
                token = null;
            }
        }
    }
}
