namespace RobotCompetitionBooth.Web.Models;

public enum BluetoothConnectionPhase
{
    Disconnected,
    Pairing,
    Connecting,
    Provisioning,
    Connected,
    Reconnecting,
    Disconnecting,
    Failed
}

public sealed record BluetoothConnectionState(
    BluetoothConnectionPhase Phase,
    string? DeviceName,
    string? DeviceAddress,
    string StatusMessage,
    DateTimeOffset LastChanged)
{
    public static BluetoothConnectionState Initial { get; } = new(
        BluetoothConnectionPhase.Disconnected,
        null,
        null,
        "No Bluetooth device is connected.",
        DateTimeOffset.Now);

    public bool IsConnected => Phase == BluetoothConnectionPhase.Connected;

    public bool IsBusy => Phase is
        BluetoothConnectionPhase.Pairing or
        BluetoothConnectionPhase.Connecting or
        BluetoothConnectionPhase.Provisioning or
        BluetoothConnectionPhase.Disconnecting;

    public bool CanDisconnect => Phase is
        BluetoothConnectionPhase.Connected or
        BluetoothConnectionPhase.Reconnecting or
        BluetoothConnectionPhase.Failed;
}

public sealed record BluetoothConnectionResult(bool Succeeded, string Message);
