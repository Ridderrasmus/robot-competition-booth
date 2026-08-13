namespace RobotCompetitionBooth.Web.Models;

public sealed record WifiNetworkInfo(
    string NetworkName,
    int SignalQuality,
    bool RequiresPassword,
    string SecurityLabel,
    bool IsConnected);

public sealed record WifiNetworkScanResult(
    IReadOnlyList<WifiNetworkInfo> Networks,
    string? Message = null);
