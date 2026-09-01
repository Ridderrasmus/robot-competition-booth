using System.Collections.Concurrent;

namespace RobotCompetitionBooth.Web.Services;

public sealed record RobotHardwareConfigurationStatus(
    string DeviceId,
    string RequestId,
    string State,
    string? ErrorCode,
    DateTimeOffset ReceivedAt);

public sealed class RobotHardwareConfigurationStatusService
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RobotHardwareConfigurationStatus>> waiters =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RobotHardwareConfigurationStatus> latest =
        new(StringComparer.Ordinal);

    public event EventHandler<RobotHardwareConfigurationStatus>? StatusReceived;

    public RobotHardwareConfigurationStatus? GetLatest(string deviceId) => latest.GetValueOrDefault(deviceId);

    public async Task<RobotHardwareConfigurationStatus> WaitForAsync(
        string deviceId,
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var key = GetWaiterKey(deviceId, requestId);
        var waiter = new TaskCompletionSource<RobotHardwareConfigurationStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!waiters.TryAdd(key, waiter))
        {
            throw new InvalidOperationException("A hardware configuration request with this ID is already pending.");
        }

        try
        {
            return await waiter.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            waiters.TryRemove(key, out _);
        }
    }

    public void Report(RobotHardwareConfigurationStatus status)
    {
        latest[status.DeviceId] = status;
        if (waiters.TryGetValue(GetWaiterKey(status.DeviceId, status.RequestId), out var waiter))
        {
            waiter.TrySetResult(status);
        }
        StatusReceived?.Invoke(this, status);
    }

    private static string GetWaiterKey(string deviceId, string requestId) => deviceId + "\n" + requestId;
}
