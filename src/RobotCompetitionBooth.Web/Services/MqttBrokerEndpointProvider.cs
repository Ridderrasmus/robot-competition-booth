using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace RobotCompetitionBooth.Web.Services;

public sealed class MqttBrokerEndpointProvider(
    IOptions<EmbeddedMqttOptions> options,
    MqttBrokerAccessService accessService)
{
    public MqttProvisioningSettings GetProvisioningSettings()
    {
        var configured = options.Value;
        if (configured.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new InvalidOperationException("The configured embedded MQTT port must be between 1 and 65535.");
        }

        var host = string.IsNullOrWhiteSpace(configured.AdvertisedHost)
            ? FindBestLocalAddress()
            : configured.AdvertisedHost.Trim();
        if (host.Length is < 1 or > 63 || host.Contains('\0'))
        {
            throw new InvalidOperationException("The advertised MQTT host must contain between 1 and 63 characters.");
        }

        var credentials = accessService.GetCredentials();
        return new(host, checked((ushort)configured.Port), credentials.Username, credentials.Password);
    }

    private static string FindBestLocalAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .Where(network => network.NetworkInterfaceType is not
                NetworkInterfaceType.Loopback and not
                NetworkInterfaceType.Tunnel)
            .SelectMany(network =>
            {
                var properties = network.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !gateway.Address.Equals(IPAddress.Any));
                return properties.UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Where(address => !IPAddress.IsLoopback(address.Address))
                    .Where(address => !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(address => new
                    {
                        Address = address.Address,
                        Score = (hasGateway ? 100 : 0) + network.NetworkInterfaceType switch
                        {
                            NetworkInterfaceType.Wireless80211 => 20,
                            NetworkInterfaceType.Ethernet => 10,
                            _ => 0
                        }
                    });
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .ToArray();

        return candidates.FirstOrDefault()?.Address.ToString()
            ?? throw new InvalidOperationException(
                "No LAN IPv4 address is available for MQTT provisioning. Set EmbeddedMqtt:AdvertisedHost explicitly.");
    }
}
