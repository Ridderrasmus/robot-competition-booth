using System.Text;
using System.Text.Json;

namespace RobotCompetitionBooth.Web.Services;

public sealed class DeviceProgramStore
{
    internal const int MaximumWorkspaceFileLength = 4 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly SemaphoreSlim storageLock = new(1, 1);
    private readonly string storageRootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RobotCompetitionBooth",
        "device-programs");

    public async Task<SavedDeviceWorkspace?> LoadAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var workspaceFilePath = GetWorkspaceFilePath(deviceId);

        await storageLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(workspaceFilePath))
            {
                return null;
            }

            var fileInfo = new FileInfo(workspaceFilePath);
            if (fileInfo.Length is <= 0 or > MaximumWorkspaceFileLength)
            {
                throw new InvalidDataException("The saved Blockly workspace has an invalid size.");
            }

            var workspaceJson = await File.ReadAllTextAsync(
                workspaceFilePath,
                StrictUtf8,
                cancellationToken);
            ValidateWorkspaceJson(workspaceJson);

            return new SavedDeviceWorkspace(
                workspaceJson,
                new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero));
        }
        finally
        {
            storageLock.Release();
        }
    }

    public async Task<DateTimeOffset> SaveAsync(
        string deviceId,
        string workspaceJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceJson);
        ValidateDeviceId(deviceId);

        if (StrictUtf8.GetByteCount(workspaceJson) is <= 0 or > MaximumWorkspaceFileLength)
        {
            throw new InvalidDataException("The Blockly workspace is too large to save.");
        }

        ValidateWorkspaceJson(workspaceJson);

        var workspaceFilePath = GetWorkspaceFilePath(deviceId);
        var deviceDirectoryPath = Path.GetDirectoryName(workspaceFilePath)
            ?? throw new InvalidOperationException("The device program directory could not be resolved.");
        var temporaryFilePath = Path.Combine(deviceDirectoryPath, $".{Guid.NewGuid():N}.tmp");

        await storageLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(deviceDirectoryPath);
            try
            {
                await File.WriteAllTextAsync(
                    temporaryFilePath,
                    workspaceJson,
                    StrictUtf8,
                    cancellationToken);
                File.Move(temporaryFilePath, workspaceFilePath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryFilePath);
            }

            return new DateTimeOffset(File.GetLastWriteTimeUtc(workspaceFilePath), TimeSpan.Zero);
        }
        finally
        {
            storageLock.Release();
        }
    }

    private string GetWorkspaceFilePath(string deviceId)
    {
        ValidateDeviceId(deviceId);
        return Path.Combine(storageRootPath, deviceId, "workspace.json");
    }

    private static void ValidateDeviceId(string? deviceId)
    {
        if (deviceId is not { Length: >= 12 and <= 64 } ||
            !deviceId.StartsWith("robotbooth-", StringComparison.Ordinal) ||
            deviceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("The Robobooth device ID is invalid.", nameof(deviceId));
        }
    }

    private static void ValidateWorkspaceJson(string workspaceJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                workspaceJson,
                new JsonDocumentOptions { MaxDepth = 128 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The Blockly workspace must be a JSON object.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Blockly workspace is not valid JSON.", exception);
        }
    }
}

public sealed record SavedDeviceWorkspace(
    string WorkspaceJson,
    DateTimeOffset LastSavedAt);
