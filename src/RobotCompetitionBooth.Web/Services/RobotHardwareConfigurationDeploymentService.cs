using RobotCompetitionBooth.Web.Models;

namespace RobotCompetitionBooth.Web.Services;

public sealed record RobotHardwareConfigurationDeploymentResult(
    bool Saved,
    bool Applied,
    string Message);

public sealed class RobotHardwareConfigurationDeploymentService(
    RobotHardwareConfigurationStore store,
    RobotHardwareConfigurationStatusService statuses,
    RobotDeviceStateService devices,
    EmbeddedMqttBrokerService broker)
{
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(8);

    public async Task<RobotHardwareConfigurationDeploymentResult> SaveAndApplyAsync(
        RobotHardwareConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await store.SaveAsync(configuration, cancellationToken);

        var requestId = Guid.NewGuid().ToString("N");
        var payload = configuration.ToMqttPayload(requestId);
        var online = devices.GetDevice(configuration.DeviceId)?.IsOnline is true;
        Task<RobotHardwareConfigurationStatus>? acknowledgement = online
            ? statuses.WaitForAsync(configuration.DeviceId, requestId, ApplyTimeout, cancellationToken)
            : null;

        await broker.PublishToDeviceAsync(
            configuration.DeviceId,
            "hardware/config",
            payload,
            retain: true);

        if (acknowledgement is null)
        {
            return new(
                true,
                false,
                "Saved on this server. The configuration will be sent when the robot reconnects.");
        }

        try
        {
            var status = await acknowledgement;
            if (string.Equals(status.State, "applied", StringComparison.Ordinal))
            {
                return new(true, true, "Saved on this server and applied by the robot.");
            }
            return new(
                true,
                false,
                $"Saved on this server, but the robot rejected it ({status.ErrorCode ?? "unknown error"}).");
        }
        catch (TimeoutException)
        {
            return new(
                true,
                false,
                "Saved and sent, but the robot did not acknowledge it. Flash the current firmware or reconnect the robot.");
        }
    }
}
