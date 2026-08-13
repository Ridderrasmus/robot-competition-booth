namespace RobotCompetitionBooth.Web.Services;

public sealed class EmbeddedMqttOptions
{
    public const string SectionName = "EmbeddedMqtt";

    public int Port { get; set; } = 1883;

    public string? AdvertisedHost { get; set; }
}

public sealed record MqttProvisioningSettings(
    string Host,
    ushort Port,
    string Username,
    string Password);
