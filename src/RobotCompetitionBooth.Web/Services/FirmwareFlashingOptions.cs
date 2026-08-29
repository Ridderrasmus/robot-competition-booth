namespace RobotCompetitionBooth.Web.Services;

public sealed class FirmwareFlashingOptions
{
    public const string SectionName = "FirmwareFlashing";

    public bool Enabled { get; set; } = true;

    public string PlatformIoExecutable { get; set; } = "pio";

    public string ProjectDirectory { get; set; } = string.Empty;

    public string Environment { get; set; } = "esp32-s3-n16r8";

    public int TimeoutMinutes { get; set; } = 10;
}

public sealed record FirmwareSerialPort(
    string PortName,
    string DisplayName,
    bool IsRobotSerialPort);

public sealed record PlatformIoAvailability(
    bool IsAvailable,
    string Message,
    string? Version,
    string? ProjectDirectory);

public sealed record FirmwareFlashLine(
    long Sequence,
    DateTimeOffset ReceivedAt,
    bool IsError,
    string Text);

public sealed record FirmwareFlashState(
    bool IsRunning,
    string? PortName,
    string Message,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    bool? Succeeded);

public sealed record FirmwareFlashResult(bool Succeeded, string Message);

public sealed record FirmwareBuildConfiguration(
    string RobotName,
    string PairingCode);
