using System.Text.Json.Serialization;

namespace RobotCompetitionBooth.Web.Models;

public sealed record RobotDiagnosticLogMessage(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("uptimeMs")] long UptimeMs,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("message")] string Message);

public sealed record RobotDiagnosticLogEntry(
    long HostSequence,
    string DeviceId,
    long RobotSequence,
    long UptimeMs,
    string Level,
    string Source,
    string Message,
    DateTimeOffset ReceivedAt);
