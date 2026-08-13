namespace RobotCompetitionBooth.Web.Services;

public sealed class BluetoothShutdownService(
    BluetoothConnectionManager connectionManager) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        connectionManager.RemoveStalePairingsAsync();

    public Task StopAsync(CancellationToken cancellationToken) =>
        connectionManager.RemovePairingForShutdownAsync();
}
