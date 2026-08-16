using System.Collections.Concurrent;
using RobotCompetitionBooth.Web.Models;

namespace RobotCompetitionBooth.Web.Services;

public sealed class RobotDeviceStateService
{
    private readonly ConcurrentDictionary<string, RobotDeviceState> devices =
        new(StringComparer.Ordinal);

    public event EventHandler? StateChanged;

    public IReadOnlyList<RobotDeviceState> GetDevices() => devices.Values
        .OrderByDescending(device => device.IsOnline)
        .ThenBy(device => device.DeviceName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(device => device.DeviceId, StringComparer.Ordinal)
        .ToArray();

    public RobotDeviceState? GetDevice(string deviceId) =>
        devices.GetValueOrDefault(deviceId);

    public void SetConnectionState(string deviceId, bool isOnline)
    {
        devices.AddOrUpdate(
            deviceId,
            id => new(id, id, "#000000", 0, isOnline, null),
            (_, existing) => existing with { IsOnline = isOnline });
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateColor(
        string deviceId,
        string deviceName,
        string colorHex,
        long sequence)
    {
        var receivedAt = DateTimeOffset.Now;
        devices.AddOrUpdate(
            deviceId,
            _ => new(deviceId, deviceName, colorHex, sequence, true, receivedAt),
            (_, existing) => existing with
            {
                DeviceName = deviceName,
                ColorHex = colorHex,
                Sequence = sequence,
                IsOnline = true,
                LastReceived = receivedAt
            });
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
