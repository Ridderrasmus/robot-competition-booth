using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using MQTTnet.Server;
using MQTTnet;
using RobotCompetitionBooth.Web.Models;

namespace RobotCompetitionBooth.Web.Services;

public sealed class EmbeddedMqttBrokerService(
    IOptions<EmbeddedMqttOptions> options,
    MqttBrokerAccessService accessService,
    RobotDeviceStateService deviceState,
    ILogger<EmbeddedMqttBrokerService> logger) : BackgroundService
{
    private const string TopicPrefix = "robobooth/v1/devices/";
    private const string ColorTopicSuffix = "/state/color";
    private const string SensorSnapshotTopicSuffix = "/telemetry/sensors";
    private const string StatusTopicSuffix = "/status";
    private const string ProgramStatusTopicSuffix = "/program/status";
    private const int MaximumPayloadLength = 512;
    private const int MaximumSensorSnapshotPayloadLength = 8 * 1024;

    private static readonly HashSet<string> RuntimeModes = new(
        ["idle", "running", "fault"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> DetectedColours = new(
        ["red", "green", "blue", "yellow", "cyan", "magenta", "white", "black", "unknown"],
        StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    private MqttServer? mqttServer;

    public async Task PublishToDeviceAsync(string deviceId, string suffix, string payload, bool retain)
    {
        DeviceProgramStore.ValidateDeviceId(deviceId);
        var server = mqttServer ?? throw new InvalidOperationException("The embedded MQTT broker is not running.");
        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"{TopicPrefix}{deviceId}/{suffix}")
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(message) { SenderClientId = "robobooth-host" });
    }

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
        // Host-injected deploy/control messages are outbound commands, not robot publications.
        if (string.Equals(args.ClientId, "robobooth-host", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

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
        else if (topic.Equals(expectedPrefix + SensorSnapshotTopicSuffix, StringComparison.Ordinal))
        {
            ProcessSensorSnapshotMessage(args);
        }
        else if (topic.Equals(expectedPrefix + StatusTopicSuffix, StringComparison.Ordinal))
        {
            ProcessStatusMessage(args);
        }
        else if (topic.Equals(expectedPrefix + ProgramStatusTopicSuffix, StringComparison.Ordinal))
        {
            ProcessProgramStatusMessage(args);
        }
        else
        {
            RejectPublish(args, "topic is not allowed");
        }

        return Task.CompletedTask;
    }

    private void ProcessProgramStatusMessage(InterceptingPublishEventArgs args)
    {
        if (!TryReadPayload(args, out var payload, 4 * 1024)) return;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) || version.GetInt32() != 1 ||
                !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
                RejectPublish(args, "program status payload is invalid");
        }
        catch (JsonException) { RejectPublish(args, "program status payload is not valid JSON"); }
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

    private void ProcessSensorSnapshotMessage(InterceptingPublishEventArgs args)
    {
        if (!TryReadPayload(args, out var payload, MaximumSensorSnapshotPayloadLength))
        {
            return;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<RobotSensorSnapshot>(payload, JsonOptions);
            if (snapshot is null || !IsValidSensorSnapshot(snapshot))
            {
                RejectPublish(args, "sensor snapshot payload is invalid");
                return;
            }

            deviceState.UpdateSensors(args.ClientId, snapshot);
        }
        catch (JsonException)
        {
            RejectPublish(args, "sensor snapshot payload is not valid JSON");
        }
    }

    private bool TryReadPayload(
        InterceptingPublishEventArgs args,
        out byte[] payload,
        int maximumLength = MaximumPayloadLength)
    {
        var sequence = args.ApplicationMessage.Payload;
        if (sequence.Length <= 0 || sequence.Length > maximumLength)
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

    private static bool IsValidSensorSnapshot(RobotSensorSnapshot snapshot)
    {
        if (snapshot.Version != 1 ||
            snapshot.Sequence < 0 ||
            snapshot.UptimeMs < 0 ||
            !RuntimeModes.Contains(snapshot.Mode) ||
            snapshot.Distance is null ||
            snapshot.Colour is null ||
            snapshot.Line is null ||
            snapshot.Motors?.Left is null ||
            snapshot.Motors.Right is null ||
            snapshot.Servos?.Angles is null)
        {
            return false;
        }

        if (snapshot.Distance.Valid &&
            (snapshot.Distance.Millimetres is not { } millimetres ||
             !double.IsFinite(millimetres) || millimetres is < 0 or > 4000))
        {
            return false;
        }

        if (snapshot.Distance.Millimetres is { } optionalMillimetres &&
            (!double.IsFinite(optionalMillimetres) || optionalMillimetres is < 0 or > 4000))
        {
            return false;
        }

        if (!IsRawColourValue(snapshot.Colour.Red) ||
            !IsRawColourValue(snapshot.Colour.Green) ||
            !IsRawColourValue(snapshot.Colour.Blue) ||
            !IsRawColourValue(snapshot.Colour.Clear) ||
            !DetectedColours.Contains(snapshot.Colour.Detected) ||
            !double.IsFinite(snapshot.Colour.LightPercent) ||
            snapshot.Colour.LightPercent is < 0 or > 100)
        {
            return false;
        }

        if (snapshot.Line.Raw.Count != 5 ||
            snapshot.Line.Raw.Any(value => value is < 0 or > 4095) ||
            snapshot.Line.Normalized.Count != 5 ||
            snapshot.Line.Normalized.Any(value => !double.IsFinite(value) || value is < 0 or > 100) ||
            snapshot.Line.Pattern.Length != 5 ||
            snapshot.Line.Pattern.Any(value => value is not ('0' or '1' or '?')) ||
            snapshot.Line.Position is { } position &&
                (!double.IsFinite(position) || position is < -100 or > 100))
        {
            return false;
        }

        if (!IsValidMotorReading(snapshot.Motors.Left) ||
            !IsValidMotorReading(snapshot.Motors.Right) ||
            snapshot.Servos.Angles.Count != 5 ||
            snapshot.Servos.Angles.Any(angle =>
                angle is { } value && (!double.IsFinite(value) || value is < 0 or > 180)))
        {
            return false;
        }

        return true;
    }

    private static bool IsRawColourValue(int value) => value is >= 0 and <= 65535;

    private static bool IsValidMotorReading(MotorSensorReading reading) =>
        double.IsFinite(reading.AngleDegrees) &&
        double.IsFinite(reading.Rotations) &&
        double.IsFinite(reading.SpeedPercent) &&
        reading.SpeedPercent is >= -100 and <= 100;

    private sealed record ColorTelemetryMessage(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rgb")] string Rgb,
        [property: JsonPropertyName("sequence")] long Sequence);
}
