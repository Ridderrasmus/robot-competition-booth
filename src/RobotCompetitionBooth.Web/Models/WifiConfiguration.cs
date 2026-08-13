namespace RobotCompetitionBooth.Web.Models;

public sealed record WifiConfigurationStatus(bool IsConfigured, string? NetworkName);

public sealed class WifiCredentials(string networkName, string password)
{
    public string NetworkName { get; } = networkName;

    public string Password { get; } = password;

    public override string ToString() => $"Wi-Fi credentials for {NetworkName} (password redacted)";
}
