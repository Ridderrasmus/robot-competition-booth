namespace RobotCompetitionBooth.Web.Models;

public sealed record BluetoothDeviceInfo(
    string Id,
    string Name,
    string Address,
    bool? IsPaired,
    bool? IsConnected,
    bool IsLowEnergy,
    short? SignalStrength,
    DateTimeOffset LastSeen);

public sealed record BluetoothScanResult(
    IReadOnlyList<BluetoothDeviceInfo> Devices,
    bool IsAvailable,
    string StatusMessage);
