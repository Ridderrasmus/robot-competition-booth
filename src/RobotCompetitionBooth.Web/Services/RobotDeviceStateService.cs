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
            id => new(id, id, "#000000", 0, isOnline, null, null),
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
            _ => new(deviceId, deviceName, colorHex, sequence, true, receivedAt, null),
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

    public void UpdateSensors(string deviceId, RobotSensorSnapshot snapshot)
    {
        var receivedSnapshot = snapshot with { ReceivedAt = DateTimeOffset.Now };
        var updated = devices.AddOrUpdate(
            deviceId,
            id => new(id, id, "#000000", 0, true, receivedSnapshot.ReceivedAt, receivedSnapshot),
            (_, existing) =>
            {
                if (existing.Sensors is not null &&
                    snapshot.Sequence <= existing.Sensors.Sequence &&
                    snapshot.UptimeMs >= existing.Sensors.UptimeMs)
                {
                    return existing;
                }

                return existing with
                {
                    IsOnline = true,
                    LastReceived = receivedSnapshot.ReceivedAt,
                    Sensors = receivedSnapshot
                };
            });

        if (ReferenceEquals(updated.Sensors, receivedSnapshot))
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
