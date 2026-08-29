using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MQTTnet;
using MQTTnet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RobotCompetitionBooth.Web.Models;
using RobotCompetitionBooth.Web.Services;

if (args.Length is < 2 or > 6)
{
    Console.Error.WriteLine(
        "Usage: direct-deploy <device-id> <workspace.json> [host] [port] [ble-address] [pairing-code]");
    return 2;
}

var deviceId = args[0];
var workspacePath = Path.GetFullPath(args[1]);
var host = args.Length >= 3 ? args[2] : "127.0.0.1";
var port = args.Length >= 4 ? int.Parse(args[3]) : 1883;
var bleAddress = args.Length >= 5 ? args[4] : null;
var pairingCode = args.Length >= 6 ? args[5] : null;
var workspaceJson = await File.ReadAllTextAsync(workspacePath, Encoding.UTF8);
var compiled = new WorkspaceCompiler().Compile(workspaceJson, Path.GetFileNameWithoutExtension(workspacePath));
var package = JsonNode.Parse(compiled.PackageJson)!;
var deploy = new JsonObject
{
    ["version"] = 1,
    ["requestId"] = Guid.NewGuid().ToString(),
    ["sentAt"] = DateTimeOffset.UtcNow.ToString("O"),
    ["packageSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compiled.PackageJson))).ToLowerInvariant(),
    ["package"] = package
}.ToJsonString();
var control = JsonSerializer.Serialize(new
{
    version = 1,
    requestId = Guid.NewGuid(),
    sentAt = DateTimeOffset.UtcNow,
    action = "run",
    programId = compiled.ProgramId
});

using var access = new MqttBrokerAccessService();
using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
}));
await using var bluetooth = bleAddress is null
    ? null
    : new BluetoothConnectionManager(
        new WifiCredentialStore(),
        new MqttBrokerEndpointProvider(
            Options.Create(new EmbeddedMqttOptions { Port = port }),
            access),
        loggerFactory.CreateLogger<BluetoothConnectionManager>());

if (bluetooth is not null)
{
    if (string.IsNullOrWhiteSpace(pairingCode))
    {
        Console.Error.WriteLine("A pairing code is required when a BLE address is supplied.");
        return 2;
    }

    var robot = new BluetoothDeviceInfo(
        bleAddress!,
        "RobotBooth-ESP32S3",
        bleAddress!,
        null,
        null,
        true,
        null,
        DateTimeOffset.Now);
    var connection = await bluetooth.ConnectAsync(robot, pairingCode);
    if (!connection.Succeeded)
    {
        Console.Error.WriteLine(connection.Message);
        return 1;
    }

    Console.WriteLine(connection.Message);
}

var credentials = access.GetCredentials();
var factory = new MqttClientFactory();
using var client = factory.CreateMqttClient();
var options = new MqttClientOptionsBuilder()
    .WithClientId("robobooth-host")
    .WithTcpServer(host, port)
    .WithCredentials(credentials.Username, credentials.Password)
    .WithCleanSession()
    .Build();

await client.ConnectAsync(options);
var topicRoot = $"robobooth/v1/devices/{deviceId}/program";
await client.PublishAsync(new MqttApplicationMessageBuilder()
    .WithTopic($"{topicRoot}/deploy")
    .WithPayload(Encoding.UTF8.GetBytes(deploy))
    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
    .Build());
await Task.Delay(250);
await client.PublishAsync(new MqttApplicationMessageBuilder()
    .WithTopic($"{topicRoot}/control")
    .WithPayload(Encoding.UTF8.GetBytes(control))
    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
    .Build());
await client.DisconnectAsync();

Console.WriteLine($"Sent {compiled.ProgramId} ({Encoding.UTF8.GetByteCount(deploy)} deploy bytes) to {deviceId}.");
if (bluetooth is not null)
{
    Console.WriteLine("The BLE connection is being held open. Press Ctrl+C after the light test is confirmed.");
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stopped.TrySetResult();
    };
    await stopped.Task;
}
return 0;
