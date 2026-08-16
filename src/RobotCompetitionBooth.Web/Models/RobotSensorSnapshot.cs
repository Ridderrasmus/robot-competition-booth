using System.Text.Json.Serialization;

namespace RobotCompetitionBooth.Web.Models;

public sealed record RobotSensorSnapshot
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("uptimeMs")]
    public long UptimeMs { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "idle";

    [JsonPropertyName("distance")]
    public DistanceSensorReading Distance { get; init; } = new();

    [JsonPropertyName("colour")]
    public ColourSensorReading Colour { get; init; } = new();

    [JsonPropertyName("line")]
    public LineSensorReading Line { get; init; } = new();

    [JsonPropertyName("motors")]
    public MotorSensorReadings Motors { get; init; } = new();

    [JsonPropertyName("servos")]
    public ServoSensorReadings Servos { get; init; } = new();

    [JsonIgnore]
    public DateTimeOffset ReceivedAt { get; init; }
}

public sealed record DistanceSensorReading
{
    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    [JsonPropertyName("millimetres")]
    public double? Millimetres { get; init; }
}

public sealed record ColourSensorReading
{
    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    [JsonPropertyName("red")]
    public int Red { get; init; }

    [JsonPropertyName("green")]
    public int Green { get; init; }

    [JsonPropertyName("blue")]
    public int Blue { get; init; }

    [JsonPropertyName("clear")]
    public int Clear { get; init; }

    [JsonPropertyName("detected")]
    public string Detected { get; init; } = "unknown";

    [JsonPropertyName("lightPercent")]
    public double LightPercent { get; init; }
}

public sealed record LineSensorReading
{
    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    [JsonPropertyName("raw")]
    public IReadOnlyList<int> Raw { get; init; } = [];

    [JsonPropertyName("normalized")]
    public IReadOnlyList<double> Normalized { get; init; } = [];

    [JsonPropertyName("pattern")]
    public string Pattern { get; init; } = "?????";

    [JsonPropertyName("position")]
    public double? Position { get; init; }
}

public sealed record MotorSensorReadings
{
    [JsonPropertyName("left")]
    public MotorSensorReading Left { get; init; } = new();

    [JsonPropertyName("right")]
    public MotorSensorReading Right { get; init; } = new();
}

public sealed record MotorSensorReading
{
    [JsonPropertyName("count")]
    public long Count { get; init; }

    [JsonPropertyName("angleDegrees")]
    public double AngleDegrees { get; init; }

    [JsonPropertyName("rotations")]
    public double Rotations { get; init; }

    [JsonPropertyName("speedPercent")]
    public double SpeedPercent { get; init; }
}

public sealed record ServoSensorReadings
{
    [JsonPropertyName("angles")]
    public IReadOnlyList<double?> Angles { get; init; } = [];
}
