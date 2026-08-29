using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RobotCompetitionBooth.Web.Services;

public sealed class RobotProgramDeploymentService(WorkspaceCompiler compiler, EmbeddedMqttBrokerService broker)
{
    public async Task<string> CompileDeployAndRunAsync(string deviceId, string workspaceName, string workspaceJson)
    {
        DeviceProgramStore.ValidateDeviceId(deviceId);
        var compiled = compiler.Compile(workspaceJson, workspaceName);
        var package = JsonNode.Parse(compiled.PackageJson)!;
        var envelope = new JsonObject
        {
            ["version"] = 1, ["requestId"] = Guid.NewGuid().ToString(), ["sentAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["packageSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compiled.PackageJson))).ToLowerInvariant(),
            ["package"] = package
        };
        await broker.PublishToDeviceAsync(deviceId, "program/deploy", envelope.ToJsonString(), retain: false);
        var control = JsonSerializer.Serialize(new { version = 1, requestId = Guid.NewGuid(), sentAt = DateTimeOffset.UtcNow, action = "run", programId = compiled.ProgramId });
        await broker.PublishToDeviceAsync(deviceId, "program/control", control, retain: false);
        return compiled.ProgramId;
    }

    public Task StopAsync(string deviceId) => broker.PublishToDeviceAsync(deviceId, "program/control",
        JsonSerializer.Serialize(new { version = 1, requestId = Guid.NewGuid(), sentAt = DateTimeOffset.UtcNow, action = "stop" }), retain: false);
}
