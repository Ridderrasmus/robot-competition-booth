namespace RobotCompetitionBooth.Web.Models;

public sealed record RobotDeviceState(
    string DeviceId,
    string DeviceName,
    string ColorHex,
    long Sequence,
    bool IsOnline,
    DateTimeOffset? LastReceived);
