using System.Text.Json;
using RobotCompetitionBooth.Web.Services;

namespace RobotCompetitionBooth.Web.Models;

public enum RobotPinCapability
{
    Output,
    DigitalInput,
    AnalogInput
}

public sealed record RobotHardwarePinDefinition(
    string Key,
    string Label,
    RobotPinCapability Capability);

public sealed record RobotHardwareComponentDefinition(
    string Key,
    string Label,
    string Description,
    IReadOnlyList<RobotHardwarePinDefinition> Pins);

public sealed class RobotHardwareConfiguration
{
    public const int ContractVersion = 1;

    private static readonly int[] OutputPins =
        [1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 21, 38, 39, 40, 41, 42, 43, 44, 47];
    private static readonly int[] DigitalInputPins = OutputPins;
    private static readonly int[] AnalogInputPins = [1, 2, 4, 5, 6, 7, 8, 9, 10];

    public static IReadOnlyList<RobotHardwareComponentDefinition> ComponentDefinitions { get; } =
    [
        Component("leftMotor", "Left motor", "PWM speed and direction outputs.",
            Output("leftMotorPwm", "PWM / speed"), Output("leftMotorDirection", "Direction")),
        Component("rightMotor", "Right motor", "PWM speed and direction outputs.",
            Output("rightMotorPwm", "PWM / speed"), Output("rightMotorDirection", "Direction")),
        Component("leftEncoder", "Left motor encoder", "Quadrature encoder channels for the left motor.",
            Input("leftEncoderA", "Channel A"), Input("leftEncoderB", "Channel B")),
        Component("rightEncoder", "Right motor encoder", "Quadrature encoder channels for the right motor.",
            Input("rightEncoderA", "Channel A"), Input("rightEncoderB", "Channel B")),
        Component("servo1", "Servo 1", "PWM signal output for servo 1.", Output("servo1", "Signal")),
        Component("servo2", "Servo 2", "PWM signal output for servo 2.", Output("servo2", "Signal")),
        Component("servo3", "Servo 3", "PWM signal output for servo 3.", Output("servo3", "Signal")),
        Component("servo4", "Servo 4", "PWM signal output for servo 4.", Output("servo4", "Signal")),
        Component("servo5", "Servo 5", "PWM signal output for servo 5.", Output("servo5", "Signal")),
        Component("distanceSensor", "Distance sensor", "Ultrasonic trigger and echo pins.",
            Output("distanceTrigger", "Trigger"), Input("distanceEcho", "Echo")),
        Component("colourSensor", "Colour sensor", "I²C data and clock pins.",
            Output("colourSda", "SDA"), Output("colourScl", "SCL")),
        Component("lineSensorArray", "Five-channel line sensor", "Analogue inputs ordered from left outer to right outer.",
            Analog("lineLeftOuter", "Left outer"), Analog("lineLeft", "Left"), Analog("lineCentre", "Centre"),
            Analog("lineRight", "Right"), Analog("lineRightOuter", "Right outer"))
    ];

    private static readonly IReadOnlyDictionary<string, RobotHardwareComponentDefinition> ComponentsByKey =
        ComponentDefinitions.ToDictionary(component => component.Key, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, RobotHardwarePinDefinition> PinsByKey =
        ComponentDefinitions.SelectMany(component => component.Pins)
            .ToDictionary(pin => pin.Key, StringComparer.Ordinal);

    public int Version { get; init; } = ContractVersion;
    public string DeviceId { get; init; } = string.Empty;
    public Dictionary<string, int> Pins { get; init; } = new(StringComparer.Ordinal);

    public static RobotHardwareConfiguration Empty(string deviceId) => new() { DeviceId = deviceId };

    public RobotHardwareConfiguration Clone() => new()
    {
        Version = Version,
        DeviceId = DeviceId,
        Pins = new(Pins, StringComparer.Ordinal)
    };

    public IReadOnlyList<RobotHardwareComponentDefinition> GetConfiguredComponents() =>
        ComponentDefinitions.Where(component => component.Pins.Any(pin => Pins.ContainsKey(pin.Key))).ToArray();

    public bool HasComponent(RobotHardwareComponentDefinition component) =>
        component.Pins.Any(pin => Pins.ContainsKey(pin.Key));

    public int? GetPin(string pinKey) =>
        Pins.TryGetValue(pinKey, out var pin) && pin >= 0 ? pin : null;

    public static IReadOnlyList<int> GetAvailablePins(RobotPinCapability capability) => capability switch
    {
        RobotPinCapability.Output => OutputPins,
        RobotPinCapability.DigitalInput => DigitalInputPins,
        RobotPinCapability.AnalogInput => AnalogInputPins,
        _ => []
    };

    public string? Validate()
    {
        if (Version != ContractVersion)
        {
            return "The hardware configuration version is not supported.";
        }

        try
        {
            DeviceProgramStore.ValidateDeviceId(DeviceId);
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }

        foreach (var key in Pins.Keys)
        {
            if (!PinsByKey.ContainsKey(key))
            {
                return $"The pin role '{key}' is not supported.";
            }
        }

        foreach (var component in ComponentDefinitions)
        {
            var assignedCount = component.Pins.Count(pin => Pins.TryGetValue(pin.Key, out var value) && value >= 0);
            var presentCount = component.Pins.Count(pin => Pins.ContainsKey(pin.Key));
            if (presentCount == 0)
            {
                continue;
            }
            if (presentCount != component.Pins.Count || assignedCount != component.Pins.Count)
            {
                return $"Assign every pin required by {component.Label}, or remove that component.";
            }
        }

        var usedPins = new Dictionary<int, string>();
        foreach (var (key, pin) in Pins)
        {
            var definition = PinsByKey[key];
            if (!GetAvailablePins(definition.Capability).Contains(pin))
            {
                return $"GPIO {pin} cannot be used for {definition.Label}.";
            }
            if (usedPins.TryGetValue(pin, out var otherKey))
            {
                return $"GPIO {pin} is assigned to both {PinsByKey[otherKey].Label} and {definition.Label}.";
            }
            usedPins.Add(pin, key);
        }

        return null;
    }

    public string ToMqttPayload(string requestId)
    {
        if (Validate() is { } error)
        {
            throw new InvalidOperationException(error);
        }
        return JsonSerializer.Serialize(new
        {
            version = ContractVersion,
            requestId,
            pins = Pins.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        });
    }

    public static bool TryGetComponent(string key, out RobotHardwareComponentDefinition definition) =>
        ComponentsByKey.TryGetValue(key, out definition!);

    private static RobotHardwareComponentDefinition Component(
        string key,
        string label,
        string description,
        params RobotHardwarePinDefinition[] pins) => new(key, label, description, pins);

    private static RobotHardwarePinDefinition Output(string key, string label) =>
        new(key, label, RobotPinCapability.Output);
    private static RobotHardwarePinDefinition Input(string key, string label) =>
        new(key, label, RobotPinCapability.DigitalInput);
    private static RobotHardwarePinDefinition Analog(string key, string label) =>
        new(key, label, RobotPinCapability.AnalogInput);
}
