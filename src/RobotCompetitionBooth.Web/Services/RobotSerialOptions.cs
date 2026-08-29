namespace RobotCompetitionBooth.Web.Services;

public sealed class RobotSerialOptions
{
    public const string SectionName = "RobotSerial";

    public bool Enabled { get; set; } = true;

    public string PortName { get; set; } = "COM5";

    public int BaudRate { get; set; } = 115200;
}

public sealed record RobotSerialLine(
    long Sequence,
    DateTimeOffset ReceivedAt,
    string Text);

public sealed record RobotSerialConnectionState(
    bool IsConnected,
    string? PortName,
    string Message);
