using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RobotCompetitionBooth.Web.Services;

public sealed partial class WorkspaceCompiler
{
    private const int MaximumBlocks = 1000;
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    private int blockCount;
    private int generatedId;

    public CompiledWorkspace Compile(string workspaceJson, string programName)
    {
        ids.Clear();
        blockCount = generatedId = 0;
        var workspace = JsonNode.Parse(workspaceJson, documentOptions: new() { MaxDepth = 128 })?.AsObject()
            ?? throw new InvalidDataException("The Blockly workspace must be an object.");
        var roots = workspace["blocks"]?["blocks"]?.AsArray()
            ?? throw new InvalidDataException("The Blockly workspace does not contain blocks.blocks.");
        var name = DeviceProgramStore.NormalizeWorkspaceName(programName);
        if (roots.Count(node => node?["type"]?.GetValue<string>() is "prg_on_start" or "robot_start") > 1)
            throw new InvalidDataException("A program can contain only one on-start stack.");

        var canonical = workspace.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var workspaceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var safeName = ProgramNameRegex().Replace(name, "-").Trim('-', '.', '_');
        var entrypoints = new JsonArray();
        foreach (var root in roots.Where(node => node is not null).OrderBy(node => node!["y"]?.GetValue<double>() ?? 0)
                     .ThenBy(node => node!["x"]?.GetValue<double>() ?? 0))
        {
            var block = root!.AsObject();
            entrypoints.Add(new JsonObject { ["kind"] = EntrypointKind(block["type"]?.GetValue<string>()), ["root"] = CompileBlock(block, 0) });
        }

        var package = new JsonObject
        {
            ["contractVersion"] = 1,
            ["programId"] = $"{(safeName.Length > 48 ? safeName[..48] : safeName)}-{workspaceHash[..12]}",
            ["programName"] = name,
            ["workspaceSha256"] = workspaceHash,
            ["compiledAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["compiler"] = new JsonObject { ["name"] = "robobooth-dotnet", ["version"] = "1.0.0" },
            ["target"] = new JsonObject { ["runtime"] = "robobooth", ["minimumRuntimeVersion"] = "1.0.0" },
            ["safety"] = new JsonObject { ["stopAllOutputsOnEnd"] = true, ["stopAllOutputsOnFault"] = true },
            ["variables"] = workspace["variables"]?.DeepClone() ?? new JsonArray(),
            ["entrypoints"] = entrypoints
        };
        var json = package.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return new(package["programId"]!.GetValue<string>(), json);
    }

    private JsonObject CompileBlock(JsonObject block, int depth)
    {
        if (depth > 128 || ++blockCount > MaximumBlocks) throw new InvalidDataException("The workspace exceeds the program size limits.");
        var opcode = block["type"]?.GetValue<string>();
        if (opcode is null || !OpcodeRegex().IsMatch(opcode)) throw new InvalidDataException($"Invalid block type: {opcode}.");
        if (opcode.StartsWith("con_", StringComparison.Ordinal))
            throw new InvalidDataException("Console blocks are not available in robot programs.");
        var id = block["id"]?.GetValue<string>() ?? $"generated-{++generatedId}";
        if (!ids.Add(id)) throw new InvalidDataException($"Duplicate block id: {id}.");
        var result = new JsonObject { ["id"] = id, ["opcode"] = opcode };
        if (block["fields"] is JsonObject fields && fields.Count > 0) result["fields"] = fields.DeepClone();
        if (block["inputs"] is JsonObject inputs)
        {
            var compiledInputs = new JsonObject();
            foreach (var input in inputs)
            {
                var connection = input.Value as JsonObject;
                var child = connection?["block"] as JsonObject ?? connection?["shadow"] as JsonObject;
                if (child is not null) compiledInputs[input.Key] = CompileBlock(child, depth + 1);
            }
            if (compiledInputs.Count > 0) result["inputs"] = compiledInputs;
        }
        if (block["enabled"]?.GetValue<bool>() is false) result["disabled"] = true;
        if (block["next"]?["block"] is JsonObject next) result["next"] = CompileBlock(next, depth + 1);
        return result;
    }

    private static string EntrypointKind(string? opcode) => opcode switch
    {
        "prg_on_start" or "robot_start" => "onStart",
        "prg_forever" => "forever",
        _ when opcode?.StartsWith("procedures_def", StringComparison.Ordinal) is true => "function",
        _ when opcode?.StartsWith("dst_on_", StringComparison.Ordinal) is true || opcode?.StartsWith("clr_on_", StringComparison.Ordinal) is true || opcode?.StartsWith("lin_on_", StringComparison.Ordinal) is true || opcode?.StartsWith("com_on_", StringComparison.Ordinal) is true => "event",
        _ => "looseStack"
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{0,79}$", RegexOptions.CultureInvariant)] private static partial Regex OpcodeRegex();
    [GeneratedRegex("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant)] private static partial Regex ProgramNameRegex();
}

public sealed record CompiledWorkspace(string ProgramId, string PackageJson);
