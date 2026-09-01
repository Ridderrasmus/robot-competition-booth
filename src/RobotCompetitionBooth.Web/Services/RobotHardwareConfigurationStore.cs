using System.Text.Json;
using RobotCompetitionBooth.Web.Models;

namespace RobotCompetitionBooth.Web.Services;

public sealed class RobotHardwareConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    private readonly SemaphoreSlim storageLock = new(1, 1);
    private readonly string storageRootPath;

    public RobotHardwareConfigurationStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobotCompetitionBooth",
            "hardware-configurations"))
    {
    }

    internal RobotHardwareConfigurationStore(string storageRootPath)
    {
        this.storageRootPath = Path.GetFullPath(storageRootPath);
    }

    public async Task<IReadOnlyList<string>> ListDeviceIdsAsync(CancellationToken cancellationToken = default)
    {
        await storageLock.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(storageRootPath))
            {
                return [];
            }

            return Directory.EnumerateFiles(storageRootPath, "robotbooth-*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(deviceId => deviceId is not null)
                .Select(deviceId => deviceId!)
                .Where(IsValidDeviceId)
                .OrderBy(deviceId => deviceId, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            storageLock.Release();
        }
    }

    public async Task<RobotHardwareConfiguration?> LoadAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        DeviceProgramStore.ValidateDeviceId(deviceId);
        await storageLock.WaitAsync(cancellationToken);
        try
        {
            var filePath = GetFilePath(deviceId);
            if (!File.Exists(filePath))
            {
                return null;
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var configuration = await JsonSerializer.DeserializeAsync<RobotHardwareConfiguration>(
                stream,
                JsonOptions,
                cancellationToken) ?? throw new InvalidDataException("The saved hardware configuration is empty.");
            if (!string.Equals(configuration.DeviceId, deviceId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The saved hardware configuration belongs to another robot.");
            }
            if (configuration.Validate() is { } error)
            {
                throw new InvalidDataException(error);
            }
            return configuration.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The saved hardware configuration is not valid JSON.", exception);
        }
        finally
        {
            storageLock.Release();
        }
    }

    public async Task SaveAsync(
        RobotHardwareConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.Validate() is { } error)
        {
            throw new InvalidOperationException(error);
        }

        var snapshot = configuration.Clone();
        await storageLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(storageRootPath);
            var filePath = GetFilePath(snapshot.DeviceId);
            var temporaryPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                File.Move(temporaryPath, filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            storageLock.Release();
        }
    }

    private string GetFilePath(string deviceId) => Path.Combine(storageRootPath, deviceId + ".json");

    private static bool IsValidDeviceId(string deviceId)
    {
        try
        {
            DeviceProgramStore.ValidateDeviceId(deviceId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
