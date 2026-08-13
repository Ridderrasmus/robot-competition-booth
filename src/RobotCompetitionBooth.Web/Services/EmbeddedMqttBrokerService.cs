using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace RobotCompetitionBooth.Web.Services;

public sealed class EmbeddedMqttBrokerService(
    IOptions<EmbeddedMqttOptions> options,
    MqttBrokerAccessService accessService,
    RobotDeviceStateService deviceState,
    ILogger<EmbeddedMqttBrokerService> logger) : BackgroundService
{
    private const string TopicPrefix = "robobooth/v1/devices/";
    private const string ColorTopicSuffix = "/state/color";
    private const string StatusTopicSuffix = "/status";
    private const int MaximumPayloadLength = 512;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    private MqttServer? mqttServer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.Value.Port;
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("The embedded MQTT port must be between 1 and 65535.");
        }

        // Materialize the DPAPI-protected token before opening the listener so
        // startup fails clearly if secure local storage is unavailable.
        accessService.GetCredentials();

        var factory = new MqttServerFactory();
        var serverOptions = factory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(port)
            .Build();
        mqttServer = factory.CreateMqttServer(serverOptions);
        mqttServer.ValidatingConnectionAsync += ValidateConnectionAsync;
        mqttServer.InterceptingPublishAsync += InterceptPublishAsync;
        mqttServer.ClientConnectedAsync += ClientConnectedAsync;
        mqttServer.ClientDisconnectedAsync += ClientDisconnectedAsync;

        await mqttServer.StartAsync();
        logger.LogInformation("Embedded MQTT broker listening on TCP port {MqttPort}", port);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            if (mqttServer is not null)
            {
                var stopOptions = factory.CreateMqttServerStopOptionsBuilder().Build();
                await mqttServer.StopAsync(stopOptions);
                mqttServer.Dispose();
                mqttServer = null;
            }
        }
    }

    private Task ValidateConnectionAsync(ValidatingConnectionEventArgs args)
    {
        var validClientId = TryValidateDeviceId(args.ClientId);
        var authenticated = accessService.Validate(args.UserName, args.RawPassword);
        if (!validClientId || !authenticated)
        {
            args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            logger.LogWarning(
                "Rejected MQTT connection for client {ClientId} from {RemoteEndpoint}",
                args.ClientId,
                args.RemoteEndPoint);
        }

        return Task.CompletedTask;
    }

    private Task ClientConnectedAsync(ClientConnectedEventArgs args)
    {
        deviceState.SetConnectionState(args.ClientId, true);
        logger.LogInformation("Robobooth MQTT client connected: {ClientId}", args.ClientId);
        return Task.CompletedTask;
    }

    private Task ClientDisconnectedAsync(ClientDisconnectedEventArgs args)
    {
        deviceState.SetConnectionState(args.ClientId, false);
        logger.LogInformation("Robobooth MQTT client disconnected: {ClientId}", args.ClientId);
        return Task.CompletedTask;
    }

    private Task InterceptPublishAsync(InterceptingPublishEventArgs args)
    {
        var expectedPrefix = $"{TopicPrefix}{args.ClientId}";
        var topic = args.ApplicationMessage.Topic;
        if (!topic.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            topic.Length <= expectedPrefix.Length)
        {
            RejectPublish(args, "topic does not belong to the authenticated client");
            return Task.CompletedTask;
        }

        if (topic.Equals(expectedPrefix + ColorTopicSuffix, StringComparison.Ordinal))
        {
            ProcessColorMessage(args);
        }
        else if (topic.Equals(expectedPrefix + StatusTopicSuffix, StringComparison.Ordinal))
        {
            ProcessStatusMessage(args);
        }
        else
        {
            RejectPublish(args, "topic is not allowed");
        }

        return Task.CompletedTask;
    }

    private void ProcessColorMessage(InterceptingPublishEventArgs args)
    {
        if (!TryReadPayload(args, out var payload))
        {
            return;
        }

        try
        {
            var message = JsonSerializer.Deserialize<ColorTelemetryMessage>(payload, JsonOptions);
            if (message is null ||
                string.IsNullOrWhiteSpace(message.Name) ||
                message.Name.Length > 64 ||
                !IsColorHex(message.Rgb) ||
                message.Sequence < 0)
            {
                RejectPublish(args, "colour payload is invalid");
                return;
            }

            deviceState.UpdateColor(args.ClientId, message.Name, message.Rgb.ToUpperInvariant(), message.Sequence);
        }
        catch (JsonException)
        {
            RejectPublish(args, "colour payload is not valid JSON");
        }
    }

    private void ProcessStatusMessage(InterceptingPublishEventArgs args)
    {
        if (!TryReadPayload(args, out var payload))
        {
            return;
        }

        var status = Encoding.UTF8.GetString(payload);
        if (string.Equals(status, "online", StringComparison.Ordinal))
        {
            deviceState.SetConnectionState(args.ClientId, true);
        }
        else if (string.Equals(status, "offline", StringComparison.Ordinal))
        {
            deviceState.SetConnectionState(args.ClientId, false);
        }
        else
        {
            RejectPublish(args, "status payload is invalid");
        }
    }

    private bool TryReadPayload(InterceptingPublishEventArgs args, out byte[] payload)
    {
        var sequence = args.ApplicationMessage.Payload;
        if (sequence.Length is <= 0 or > MaximumPayloadLength)
        {
            payload = [];
            RejectPublish(args, "payload size is invalid");
            return false;
        }

        payload = sequence.ToArray();
        return true;
    }

    private void RejectPublish(InterceptingPublishEventArgs args, string reason)
    {
        args.ProcessPublish = false;
        args.CloseConnection = true;
        logger.LogWarning(
            "Rejected MQTT publish from {ClientId} on {Topic}: {Reason}",
            args.ClientId,
            args.ApplicationMessage.Topic,
            reason);
    }

    private static bool TryValidateDeviceId(string? deviceId) =>
        deviceId is { Length: >= 12 and <= 64 } &&
        deviceId.StartsWith("robotbooth-", StringComparison.Ordinal) &&
        deviceId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsColorHex(string? value) =>
        value is { Length: 7 } &&
        value[0] == '#' &&
        value.AsSpan(1).ContainsAnyExcept(SearchValues.Create("0123456789abcdefABCDEF")) is false;

    private sealed record ColorTelemetryMessage(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rgb")] string Rgb,
        [property: JsonPropertyName("sequence")] long Sequence);
}
