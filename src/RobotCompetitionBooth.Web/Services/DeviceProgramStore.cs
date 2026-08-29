using System.Text;
using System.Text.Json;

namespace RobotCompetitionBooth.Web.Services;

public sealed class DeviceProgramStore
{
    internal const int MaximumWorkspaceFileLength = 4 * 1024 * 1024;

    private const int MaximumWorkspaceNameLength = 80;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ReservedWindowsFileNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim storageLock = new(1, 1);
    private readonly string storageRootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RobotCompetitionBooth",
        "device-programs");

    public async Task<IReadOnlyList<SavedDeviceProgramInfo>> ListAllAsync(
        CancellationToken cancellationToken = default)
    {
        await storageLock.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(storageRootPath))
            {
                return [];
            }

            var savedPrograms = new List<SavedDeviceProgramInfo>();
            foreach (var deviceDirectoryPath in Directory.EnumerateDirectories(
                         storageRootPath,
                         "robotbooth-*",
                         SearchOption.TopDirectoryOnly))
            {
                var deviceId = Path.GetFileName(deviceDirectoryPath);
                try
                {
                    ValidateDeviceId(deviceId);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                foreach (var workspaceFilePath in Directory.EnumerateFiles(
                             deviceDirectoryPath,
                             "*.json",
                             SearchOption.TopDirectoryOnly))
                {
                    var file = new FileInfo(workspaceFilePath);
                    var workspaceName = Path.GetFileNameWithoutExtension(file.Name);
                    try
                    {
                        workspaceName = NormalizeWorkspaceName(workspaceName);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    savedPrograms.Add(new(
                        deviceId,
                        workspaceName,
                        new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                        file.Length));
                }
            }

            return savedPrograms
                .OrderBy(program => program.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(program => program.LastSavedAt)
                .ThenBy(program => program.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            storageLock.Release();
        }
    }

    public async Task<IReadOnlyList<SavedDeviceWorkspaceInfo>> ListAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var deviceDirectoryPath = GetDeviceDirectoryPath(deviceId);

        await storageLock.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(deviceDirectoryPath))
            {
                return [];
            }

            return Directory
                .EnumerateFiles(deviceDirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Select(file => new SavedDeviceWorkspaceInfo(
                    Path.GetFileNameWithoutExtension(file.Name),
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                    file.Length))
                .OrderByDescending(workspace => workspace.LastSavedAt)
                .ThenBy(workspace => workspace.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            storageLock.Release();
        }
    }

    public async Task<SavedDeviceWorkspace?> LoadAsync(
        string deviceId,
        string workspaceName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeWorkspaceName(workspaceName);
        var workspaceFilePath = GetWorkspaceFilePath(deviceId, normalizedName);

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
                normalizedName,
                workspaceJson,
                new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero));
        }
        finally
        {
            storageLock.Release();
        }
    }

    public async Task<SavedDeviceWorkspaceInfo> SaveAsync(
        string deviceId,
        string workspaceName,
        string workspaceJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceJson);
        var normalizedName = NormalizeWorkspaceName(workspaceName);

        ValidateWorkspacePayload(workspaceJson);

        var workspaceFilePath = GetWorkspaceFilePath(deviceId, normalizedName);
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

            var fileInfo = new FileInfo(workspaceFilePath);
            return new SavedDeviceWorkspaceInfo(
                normalizedName,
                new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
                fileInfo.Length);
        }
        finally
        {
            storageLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        string deviceId,
        string workspaceName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeWorkspaceName(workspaceName);
        var workspaceFilePath = GetWorkspaceFilePath(deviceId, normalizedName);
        var deviceDirectoryPath = Path.GetDirectoryName(workspaceFilePath)!;

        await storageLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(workspaceFilePath))
            {
                return false;
            }

            File.Delete(workspaceFilePath);
            if (Directory.Exists(deviceDirectoryPath) &&
                !Directory.EnumerateFileSystemEntries(deviceDirectoryPath).Any())
            {
                Directory.Delete(deviceDirectoryPath);
            }

            return true;
        }
        finally
        {
            storageLock.Release();
        }
    }

    private string GetWorkspaceFilePath(string deviceId, string normalizedWorkspaceName) =>
        Path.Combine(GetDeviceDirectoryPath(deviceId), $"{normalizedWorkspaceName}.json");

    private string GetDeviceDirectoryPath(string deviceId)
    {
        ValidateDeviceId(deviceId);
        return Path.Combine(storageRootPath, deviceId);
    }

    internal static string NormalizeWorkspaceName(string? workspaceName)
    {
        var normalizedName = workspaceName?.Trim() ?? string.Empty;
        if (normalizedName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            normalizedName = normalizedName[..^5].TrimEnd();
        }

        if (normalizedName.Length is < 1 or > MaximumWorkspaceNameLength)
        {
            throw new ArgumentException(
                $"The workspace name must be between 1 and {MaximumWorkspaceNameLength} characters.",
                nameof(workspaceName));
        }

        if (normalizedName.EndsWith('.') ||
            normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalizedName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The workspace name contains characters that cannot be used in a file name.",
                nameof(workspaceName));
        }

        var firstNameSegment = normalizedName.Split('.', 2)[0];
        if (ReservedWindowsFileNames.Contains(firstNameSegment))
        {
            throw new ArgumentException(
                "The workspace name is reserved by Windows. Choose another name.",
                nameof(workspaceName));
        }

        return normalizedName;
    }

    internal static void ValidateDeviceId(string? deviceId)
    {
        if (deviceId is not { Length: >= 12 and <= 64 } ||
            !deviceId.StartsWith("robotbooth-", StringComparison.Ordinal) ||
            deviceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("The Robobooth device ID is invalid.", nameof(deviceId));
        }
    }

    internal static void ValidateWorkspacePayload(string workspaceJson)
    {
        ArgumentNullException.ThrowIfNull(workspaceJson);
        if (StrictUtf8.GetByteCount(workspaceJson) is <= 0 or > MaximumWorkspaceFileLength)
        {
            throw new InvalidDataException("The Blockly workspace is too large.");
        }

        ValidateWorkspaceJson(workspaceJson);
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

public sealed record SavedDeviceWorkspaceInfo(
    string Name,
    DateTimeOffset LastSavedAt,
    long FileSize);

public sealed record SavedDeviceWorkspace(
    string Name,
    string WorkspaceJson,
    DateTimeOffset LastSavedAt);

public sealed record SavedDeviceProgramInfo(
    string DeviceId,
    string Name,
    DateTimeOffset LastSavedAt,
    long FileSize);
