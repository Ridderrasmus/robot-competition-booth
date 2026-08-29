const colours = {
    program: "#5b63c7", robot: "#1677c8", motors: "#e76f00", servos: "#8d57a5",
    distance: "#008b79", colour: "#8a3fa0", line: "#328044",
    communication: "#007f8b", console: "#536b78", "math-extra": "#5c68a6", advanced: "#455a64"
};

const motors = [["left motor", "left"], ["right motor", "right"]];
const servos = Array.from({ length: 5 }, (_, i) => [`servo ${i + 1}`, `${i + 1}`]);
const timers = [["default timer", "default"], ["timer 1", "1"], ["timer 2", "2"]];
const onOff = [["on", "on"], ["off", "off"]];
const coloursList = ["red", "green", "blue", "yellow", "cyan", "magenta", "white", "black", "unknown"]
    .map(value => [value, value]);
const lineChannels = [
    ["far left", "far-left"], ["left", "left"], ["centre", "centre"],
    ["right", "right"], ["far right", "far-right"]
];
const pins = [
    ...Array.from({ length: 18 }, (_, i) => `${i + 1}`),
    "21", ...Array.from({ length: 10 }, (_, i) => `${i + 35}`), "47"
].map(pin => [`GPIO ${pin}`, pin]);
const muxChannels = Array.from({ length: 8 }, (_, i) => [`channel ${i}`, `${i}`]);

const N = (name, value) => ({
    type: "input_value", name, check: "Number",
    shadow: { type: "math_number", fields: { NUM: value } }
});
const T = (name, value = "") => ({
    type: "input_value", name, check: "String",
    shadow: { type: "text", fields: { TEXT: value } }
});
const A = (name, value = 0) => ({
    type: "input_value", name,
    shadow: typeof value === "string"
        ? { type: "text", fields: { TEXT: value } }
        : { type: "math_number", fields: { NUM: value } }
});
const D = (name, options) => ({ type: "field_dropdown", name, options });
const C = (name, colour = "#28a745") => ({ type: "field_colour", name, colour });
const S = name => ({ type: "input_statement", name });

const specs = [];
const add = (id, type, section, priority, shape, message, args = [], output) =>
    specs.push({ id, type, section, priority, shape, message, args, output });
const cmd = (id, type, section, priority, message, args) => add(id, type, section, priority, "command", message, args);
const cfg = (id, type, section, priority, message, args) => add(id, type, section, priority, "configuration", message, args);
const rep = (id, type, section, priority, message, args, output = "Number") => add(id, type, section, priority, "reporter", message, args, output);
const bool = (id, type, section, priority, message, args) => add(id, type, section, priority, "boolean", message, args, "Boolean");
const evt = (id, type, section, priority, message, args) => add(id, type, section, priority, "event", message, args);
const box = (id, type, section, priority, message, args, top = false) => add(id, type, section, priority, top ? "top-container" : "container", message, args);

evt("PRG-001", "prg_on_start", "program", "MVP", "on start");
box("PRG-002", "prg_forever", "program", "MVP", "forever %1", [S("DO")], true);
cmd("PRG-003", "prg_pause_ms", "program", "MVP", "pause %1 ms", [N("TIME", 100)]);
cmd("PRG-004", "prg_pause_seconds", "program", "Phase 2", "pause %1 seconds", [N("TIME", 1)]);
box("PRG-005", "prg_repeat_every", "program", "Phase 2", "repeat every %1 ms %2", [N("TIME", 100), S("DO")]);
box("PRG-006", "prg_background", "program", "Advanced", "run in background %1", [S("DO")]);
cmd("PRG-007", "prg_stop", "program", "MVP", "stop program");
cmd("PRG-008", "prg_reset_timer", "program", "Phase 2", "reset %1", [D("TIMER", timers)]);
rep("PRG-009", "prg_timer_seconds", "program", "Phase 2", "%1 seconds", [D("TIMER", timers)]);
rep("PRG-010", "prg_timer_millis", "program", "Phase 2", "%1 milliseconds", [D("TIMER", timers)]);

cmd("RBT-001", "rbt_set_status_light", "robot", "MVP", "set status light to %1", [C("COLOUR")]);
cmd("RBT-002", "rbt_clear_status_light", "robot", "MVP", "clear status light");
cmd("RBT-003", "rbt_blink_status_light", "robot", "Phase 2", "blink status light %1 %2 times", [C("COLOUR", "#0d6efd"), N("TIMES", 3)]);
rep("RBT-004", "rbt_name", "robot", "Phase 2", "robot name", [], "String");
bool("RBT-005", "rbt_is_connected", "robot", "Phase 2", "robot is connected");
cmd("RBT-006", "rbt_wait_connected", "robot", "Phase 2", "wait until robot connected");
cmd("RBT-007", "rbt_calibrate", "robot", "Phase 2", "calibrate robot");
cmd("RBT-008", "rbt_reset_state", "robot", "Advanced", "reset robot state");

cmd("MOT-001", "mot_run", "motors", "MVP", "run %1 at %2 %%", [D("MOTOR", motors), N("SPEED", 50)]);
cmd("MOT-002", "mot_run_for_ms", "motors", "MVP", "run %1 at %2 %% for %3 ms", [D("MOTOR", motors), N("SPEED", 50), N("TIME", 1000)]);
cmd("MOT-003", "mot_run_for_rotations", "motors", "MVP", "run %1 at %2 %% for %3 rotations", [D("MOTOR", motors), N("SPEED", 50), N("ROTATIONS", 1)]);
cmd("MOT-004", "mot_run_for_degrees", "motors", "MVP", "run %1 at %2 %% for %3 degrees", [D("MOTOR", motors), N("SPEED", 50), N("DEGREES", 360)]);
cmd("MOT-005", "mot_stop", "motors", "MVP", "stop %1", [D("MOTOR", motors)]);
cmd("MOT-006", "mot_stop_mode", "motors", "MVP", "stop %1 with %2", [D("MOTOR", motors), D("MODE", [["brake", "brake"], ["coast", "coast"]])]);
cmd("MOT-007", "mot_stop_all", "motors", "MVP", "stop all motors");
cfg("MOT-008", "mot_set_inverted", "motors", "Phase 2", "set %1 inverted %2", [D("MOTOR", motors), D("STATE", onOff)]);
cfg("MOT-009", "mot_set_brake_mode", "motors", "Phase 2", "set %1 stop mode %2", [D("MOTOR", motors), D("MODE", [["brake", "brake"], ["coast", "coast"]])]);
cfg("MOT-010", "mot_set_regulated", "motors", "Phase 2", "set %1 regulated %2", [D("MOTOR", motors), D("STATE", onOff)]);

rep("ENC-001", "enc_angle", "encoders", "MVP", "%1 angle", [D("MOTOR", motors)]);
rep("ENC-002", "enc_rotations", "encoders", "MVP", "%1 rotations", [D("MOTOR", motors)]);
rep("ENC-003", "enc_count", "encoders", "MVP", "%1 encoder count", [D("MOTOR", motors)]);
rep("ENC-004", "enc_speed", "encoders", "MVP", "%1 speed", [D("MOTOR", motors)]);
cmd("ENC-005", "enc_reset", "encoders", "MVP", "reset %1 count", [D("MOTOR", motors)]);
cmd("ENC-006", "enc_reset_all", "encoders", "MVP", "reset all motor counts");

cmd("DRV-001", "drv_tank", "drive", "MVP", "tank drive left %1 %% right %2 %%", [N("LEFT", 50), N("RIGHT", 50)]);
cmd("DRV-002", "drv_tank_for_ms", "drive", "MVP", "tank drive left %1 %% right %2 %% for %3 ms", [N("LEFT", 50), N("RIGHT", 50), N("TIME", 1000)]);
cmd("DRV-003", "drv_tank_for_rotations", "drive", "MVP", "tank drive left %1 %% right %2 %% for %3 rotations", [N("LEFT", 50), N("RIGHT", 50), N("ROTATIONS", 1)]);
cmd("DRV-004", "drv_steer", "drive", "MVP", "steer %1 %% at %2 %%", [N("TURN", 0), N("SPEED", 50)]);
cmd("DRV-005", "drv_steer_for_ms", "drive", "Phase 2", "steer %1 %% at %2 %% for %3 ms", [N("TURN", 0), N("SPEED", 50), N("TIME", 1000)]);
cmd("DRV-006", "drv_forward", "drive", "MVP", "drive forward at %1 %%", [N("SPEED", 50)]);
cmd("DRV-007", "drv_backward", "drive", "MVP", "drive backward at %1 %%", [N("SPEED", 50)]);
cmd("DRV-008", "drv_distance", "drive", "MVP", "drive %1 cm at %2 %%", [N("DISTANCE", 20), N("SPEED", 50)]);
cmd("DRV-009", "drv_turn_left", "drive", "MVP", "turn left at %1 %%", [N("SPEED", 40)]);
cmd("DRV-010", "drv_turn_right", "drive", "MVP", "turn right at %1 %%", [N("SPEED", 40)]);
cmd("DRV-011", "drv_turn_degrees", "drive", "MVP", "turn %1 degrees at %2 %%", [N("DEGREES", 90), N("SPEED", 40)]);
cfg("DRV-012", "drv_set_wheel_diameter", "drive", "Phase 2", "set wheel diameter to %1 mm", [N("MM", 65)]);
cfg("DRV-013", "drv_set_track_width", "drive", "Phase 2", "set wheel track width to %1 mm", [N("MM", 140)]);

cmd("SRV-001", "srv_set_angle", "servos", "MVP", "set %1 angle to %2 degrees", [D("SERVO", servos), N("ANGLE", 90)]);
cmd("SRV-002", "srv_center", "servos", "MVP", "centre %1", [D("SERVO", servos)]);
cmd("SRV-003", "srv_move_smooth", "servos", "Phase 2", "move %1 to %2 degrees over %3 ms", [D("SERVO", servos), N("ANGLE", 90), N("TIME", 1000)]);
cmd("SRV-004", "srv_sweep", "servos", "Phase 2", "sweep %1 from %2 to %3 degrees", [D("SERVO", servos), N("START", 0), N("END", 180)]);
cmd("SRV-005", "srv_stop", "servos", "Phase 2", "stop %1", [D("SERVO", servos)]);
rep("SRV-006", "srv_angle", "servos", "MVP", "%1 angle", [D("SERVO", servos)]);
cfg("SRV-007", "srv_set_limits", "servos", "Phase 2", "set %1 limits min %2 max %3", [D("SERVO", servos), N("MIN", 0), N("MAX", 180)]);
cfg("SRV-008", "srv_set_pulse_range", "servos", "Advanced", "set %1 pulse range min %2 us max %3 us", [D("SERVO", servos), N("MIN", 500), N("MAX", 2500)]);
cfg("SRV-009", "srv_set_pwm_frequency", "servos", "Advanced", "set servo PWM frequency to %1 Hz", [N("HZ", 50)]);

rep("DST-001", "dst_cm", "distance", "MVP", "distance in cm");
rep("DST-002", "dst_mm", "distance", "Phase 2", "distance in mm");
bool("DST-003", "dst_closer", "distance", "MVP", "object is closer than %1 cm", [N("DISTANCE", 20)]);
bool("DST-004", "dst_farther", "distance", "MVP", "object is farther than %1 cm", [N("DISTANCE", 20)]);
evt("DST-005", "dst_on_closer", "distance", "MVP", "on object closer than %1 cm", [N("DISTANCE", 20)]);
evt("DST-006", "dst_on_farther", "distance", "Phase 2", "on object farther than %1 cm", [N("DISTANCE", 20)]);
cmd("DST-007", "dst_wait_closer", "distance", "MVP", "pause until object closer than %1 cm", [N("DISTANCE", 20)]);
cmd("DST-008", "dst_wait_farther", "distance", "Phase 2", "pause until object farther than %1 cm", [N("DISTANCE", 20)]);
cfg("DST-009", "dst_set_mode", "distance", "Advanced", "set distance mode %1", [D("MODE", [["short", "short"], ["long", "long"]])]);
cfg("DST-010", "dst_set_timing_budget", "distance", "Advanced", "set distance timing budget %1 ms", [N("TIME", 50)]);
cfg("DST-011", "dst_calibrate_offset", "distance", "Advanced", "calibrate distance offset %1 mm", [N("OFFSET", 0)]);

rep("CLR-001", "clr_detected", "colour", "MVP", "detected colour", [], "String");
rep("CLR-002", "clr_red", "colour", "MVP", "red value");
rep("CLR-003", "clr_green", "colour", "MVP", "green value");
rep("CLR-004", "clr_blue", "colour", "MVP", "blue value");
rep("CLR-005", "clr_clear", "colour", "MVP", "clear light value");
rep("CLR-006", "clr_light_level", "colour", "MVP", "light level");
bool("CLR-007", "clr_is", "colour", "MVP", "colour is %1", [D("COLOUR", coloursList)]);
evt("CLR-008", "clr_on_detected", "colour", "MVP", "on colour %1 detected", [D("COLOUR", coloursList)]);
cmd("CLR-009", "clr_wait_detected", "colour", "MVP", "pause until colour %1 detected", [D("COLOUR", coloursList)]);
evt("CLR-010", "clr_on_light", "colour", "Phase 2", "on light becomes %1", [D("LEVEL", [["dark", "dark"], ["bright", "bright"]])]);
cmd("CLR-011", "clr_wait_light", "colour", "Phase 2", "pause until light is %1", [D("LEVEL", [["dark", "dark"], ["bright", "bright"]])]);
cmd("CLR-012", "clr_calibrate", "colour", "Phase 2", "calibrate colour sensor");
cfg("CLR-013", "clr_set_gain", "colour", "Advanced", "set colour sensor gain %1", [D("GAIN", [["1x", "1"], ["4x", "4"], ["16x", "16"], ["60x", "60"]])]);
cfg("CLR-014", "clr_set_integration", "colour", "Advanced", "set colour integration time %1 ms", [N("TIME", 50)]);

bool("LIN-001", "lin_sees", "line", "MVP", "line sensor %1 sees %2", [D("CHANNEL", lineChannels), D("SURFACE", [["line", "line"], ["floor", "floor"]])]);
rep("LIN-002", "lin_pattern", "line", "MVP", "line pattern", [], "String");
rep("LIN-003", "lin_position", "line", "MVP", "line position");
bool("LIN-004", "lin_centered", "line", "MVP", "line is centred");
bool("LIN-005", "lin_lost", "line", "MVP", "line is lost");
bool("LIN-006", "lin_junction", "line", "MVP", "junction detected");
evt("LIN-007", "lin_on_detected", "line", "Phase 2", "on line detected");
evt("LIN-008", "lin_on_lost", "line", "Phase 2", "on line lost");
evt("LIN-009", "lin_on_junction", "line", "Phase 2", "on junction detected");
cmd("LIN-010", "lin_wait_detected", "line", "Phase 2", "pause until line detected");
cmd("LIN-011", "lin_calibrate_black", "line", "Phase 2", "calibrate line black");
cmd("LIN-012", "lin_calibrate_white", "line", "Phase 2", "calibrate line white");
cmd("LIN-013", "lin_follow", "line", "Phase 2", "follow line at %1 %%", [N("SPEED", 40)]);
cmd("LIN-014", "lin_follow_sensitive", "line", "Advanced", "follow line at %1 %% with sensitivity %2", [N("SPEED", 40), N("SENSITIVITY", 50)]);
bool("LIN-015", "lin_front_obstacle", "line", "Optional", "front obstacle detected");
bool("LIN-016", "lin_front_touch", "line", "Optional", "front touch detected");

cmd("COM-001", "com_send_message", "communication", "MVP", "send message %1 to dashboard", [T("TEXT", "Hello")]);
cmd("COM-002", "com_send_value", "communication", "MVP", "send value %1 = %2", [T("NAME", "value"), A("VALUE")]);
cmd("COM-003", "com_graph_value", "communication", "Phase 2", "graph value %1 = %2", [T("NAME", "value"), N("VALUE", 0)]);
evt("COM-004", "com_on_command", "communication", "Phase 2", "on command %1 received", [T("NAME", "command")]);
rep("COM-005", "com_command_value", "communication", "Phase 2", "command value", [], null);
cmd("COM-006", "com_set_field", "communication", "Phase 2", "set dashboard field %1 to %2", [T("NAME", "field"), A("VALUE")]);
bool("COM-007", "com_dashboard_connected", "communication", "Phase 2", "robot connected to dashboard");
cmd("COM-008", "com_wait_dashboard", "communication", "Phase 2", "wait until dashboard connected");
cmd("COM-009", "com_send_sensor_snapshot", "communication", "Phase 2", "send sensor snapshot");
cmd("COM-010", "com_send_motor_snapshot", "communication", "Phase 2", "send motor snapshot");

cmd("CON-001", "con_log", "console", "MVP", "log %1", [T("TEXT", "message")]);
cmd("CON-002", "con_log_value", "console", "MVP", "log value %1 = %2", [T("NAME", "value"), A("VALUE")]);
cmd("CON-003", "con_clear", "console", "Phase 2", "clear console");
cmd("CON-004", "con_show_number", "console", "Phase 2", "show number %1", [N("VALUE", 0)]);
cmd("CON-005", "con_show_string", "console", "Phase 2", "show string %1", [T("TEXT", "text")]);
cmd("CON-006", "con_show_sensors", "console", "Phase 2", "show sensor values");
cmd("CON-007", "con_show_motors", "console", "Phase 2", "show motor values");

rep("MAT-006", "mat_map", "math-extra", "MVP", "map %1 from %2 - %3 to %4 - %5", [N("VALUE", 0), N("IN_MIN", 0), N("IN_MAX", 100), N("OUT_MIN", 0), N("OUT_MAX", 1)]);
rep("MAT-007", "mat_constrain", "math-extra", "MVP", "constrain %1 between %2 and %3", [N("VALUE", 0), N("MIN", 0), N("MAX", 100)]);
rep("MAT-010", "mat_min", "math-extra", "MVP", "min of %1 and %2", [N("A", 0), N("B", 0)]);
rep("MAT-011", "mat_max", "math-extra", "MVP", "max of %1 and %2", [N("A", 0), N("B", 0)]);
rep("MAT-014", "mat_atan2", "math-extra", "Advanced", "atan2 y %1 x %2", [N("Y", 0), N("X", 1)]);
rep("MAT-015", "mat_pid", "math-extra", "Advanced", "PID error %1 kp %2 ki %3 kd %4", [N("ERROR", 0), N("KP", 1), N("KI", 0), N("KD", 0)]);

bool("ADV-001", "adv_digital_read", "hardware", "Advanced", "digital read pin %1", [D("PIN", pins)]);
cmd("ADV-002", "adv_digital_write", "hardware", "Advanced", "digital write pin %1 to %2", [D("PIN", pins), D("STATE", [["high", "high"], ["low", "low"]])]);
rep("ADV-003", "adv_analog_read", "hardware", "Advanced", "analog read pin %1", [D("PIN", pins)]);
cmd("ADV-004", "adv_pwm_write", "hardware", "Advanced", "set PWM pin %1 to %2 %%", [D("PIN", pins), N("VALUE", 50)]);
cfg("ADV-005", "adv_pwm_frequency", "hardware", "Advanced", "set PWM frequency pin %1 to %2 Hz", [D("PIN", pins), N("HZ", 1000)]);
rep("ADV-006", "adv_i2c_scan", "hardware", "Advanced", "I2C scan", [], "Array");
cfg("ADV-007", "adv_i2c_channel", "hardware", "Advanced", "select I2C %1", [D("CHANNEL", muxChannels)]);
rep("ADV-008", "adv_i2c_read", "hardware", "Advanced", "read I2C device %1 register %2", [N("ADDRESS", 41), N("REGISTER", 0)]);
cmd("ADV-009", "adv_i2c_write", "hardware", "Advanced", "write I2C device %1 register %2 value %3", [N("ADDRESS", 41), N("REGISTER", 0), N("VALUE", 0)]);
cfg("ADV-010", "adv_encoder_pins", "hardware", "Advanced", "set %1 encoder pins A %2 B %3", [D("MOTOR", motors), D("PIN_A", pins), D("PIN_B", pins)]);
cfg("ADV-011", "adv_motor_pins", "hardware", "Advanced", "set %1 driver pins IN1 %2 IN2 %3", [D("MOTOR", motors), D("PIN_1", pins), D("PIN_2", pins)]);
cfg("ADV-012", "adv_servo_channel", "hardware", "Advanced", "set %1 to PCA9685 %2", [D("SERVO", servos), D("CHANNEL", Array.from({ length: 16 }, (_, i) => [`channel ${i}`, `${i}`]))]);
cfg("ADV-013", "adv_sensor_channel", "hardware", "Advanced", "set sensor %1 to I2C %2", [D("SENSOR", [["distance", "distance"], ["colour", "colour"]]), D("CHANNEL", muxChannels)]);

const colourFor = section => colours[section] ?? (["encoders", "drive"].includes(section) ? colours.motors : colours.advanced);

function definition(spec) {
    const value = {
        type: spec.type,
        message0: spec.message,
        args0: (spec.args ?? []).map(({ shadow, ...arg }) => arg),
        inputsInline: true,
        colour: colourFor(spec.section),
        tooltip: `${spec.id} - ${spec.priority}`,
        helpUrl: ""
    };
    if (["reporter", "boolean"].includes(spec.shape)) value.output = spec.output ?? null;
    else if (spec.shape === "event") value.nextStatement = null;
    else if (spec.shape !== "top-container") {
        value.previousStatement = null;
        value.nextStatement = null;
    }
    return value;
}

function toolboxBlock(spec) {
    const inputs = Object.fromEntries((spec.args ?? []).filter(arg => arg.shadow).map(arg => [arg.name, { shadow: arg.shadow }]));
    return Object.keys(inputs).length ? { kind: "block", type: spec.type, inputs } : { kind: "block", type: spec.type };
}

const regular = section => specs
    .filter(spec => spec.section === section && !["Advanced", "Optional"].includes(spec.priority))
    .map(toolboxBlock);
const advanced = (label, sections) => [
    { kind: "label", text: label },
    ...specs.filter(spec => sections.includes(spec.section) && spec.priority === "Advanced").map(toolboxBlock),
    { kind: "sep", gap: "18" }
];
const numberShadow = value => ({ shadow: { type: "math_number", fields: { NUM: value } } });

export function defineRobotBlocks(blockly) {
    if (!blockly.fieldRegistry.getClass("field_colour", false)) {
        globalThis.registerFieldColour();
    }
    if (!blockly.Blocks.prg_on_start) blockly.common.defineBlocksWithJsonArray(specs.map(definition));
    if (!blockly.Blocks.robot_start) {
        blockly.common.defineBlocksWithJsonArray([
            {
                type: "robot_start", message0: "when program starts", nextStatement: null,
                colour: colours.program, tooltip: "Legacy on-start block.", helpUrl: ""
            },
            {
                type: "robot_set_light", message0: "set robot light to %1",
                args0: [{ type: "field_colour", name: "COLOUR", colour: "#ff0000" }],
                previousStatement: null, nextStatement: null,
                colour: colours.robot, tooltip: "Legacy status-light block.", helpUrl: ""
            },
            {
                type: "robot_wait", message0: "wait %1 milliseconds",
                args0: [{ type: "input_value", name: "DURATION", check: "Number" }],
                previousStatement: null, nextStatement: null,
                colour: colours.program, tooltip: "Legacy pause block.", helpUrl: ""
            }
        ]);
    }
}

export function createRobotToolbox() {
    return {
        kind: "categoryToolbox",
        contents: [
            { kind: "category", name: "Program", colour: colours.program, contents: regular("program") },
            { kind: "category", name: "Robot", colour: colours.robot, contents: regular("robot") },
            { kind: "category", name: "Motors & drive", colour: colours.motors, contents: [
                { kind: "label", text: "Motors" }, ...regular("motors"), { kind: "sep", gap: "16" },
                { kind: "label", text: "Encoder values" }, ...regular("encoders"), { kind: "sep", gap: "16" },
                { kind: "label", text: "Drive & steering" }, ...regular("drive")
            ] },
            { kind: "category", name: "Servos", colour: colours.servos, contents: regular("servos") },
            { kind: "category", name: "Distance", colour: colours.distance, contents: regular("distance") },
            { kind: "category", name: "Colour & light", colour: colours.colour, contents: regular("colour") },
            { kind: "category", name: "Line tracking", colour: colours.line, contents: regular("line") },
            { kind: "category", name: "Dashboard", colour: colours.communication, contents: regular("communication") },
            { kind: "category", name: "Console", colour: colours.console, contents: regular("console") },
            { kind: "category", name: "Logic", categorystyle: "logic_category", contents: [
                { kind: "block", type: "controls_if" }, { kind: "block", type: "logic_compare" },
                { kind: "block", type: "logic_operation" }, { kind: "block", type: "logic_negate" },
                { kind: "block", type: "logic_boolean" }
            ] },
            { kind: "category", name: "Loops", categorystyle: "loop_category", contents: [
                { kind: "block", type: "controls_repeat_ext", inputs: { TIMES: numberShadow(10) } },
                { kind: "block", type: "controls_whileUntil" },
                { kind: "block", type: "controls_for", inputs: { FROM: numberShadow(1), TO: numberShadow(10), BY: numberShadow(1) } },
                { kind: "block", type: "controls_forEach" }, { kind: "block", type: "controls_flow_statements" }
            ] },
            { kind: "category", name: "Math", categorystyle: "math_category", contents: [
                { kind: "block", type: "math_number", fields: { NUM: 0 } }, { kind: "block", type: "math_arithmetic" },
                { kind: "block", type: "math_random_int", inputs: { FROM: numberShadow(1), TO: numberShadow(10) } },
                ...regular("math-extra"),
                { kind: "block", type: "math_single", fields: { OP: "ABS" } }, { kind: "block", type: "math_round" },
                { kind: "block", type: "math_single", fields: { OP: "SIN" } }, { kind: "block", type: "math_single", fields: { OP: "COS" } }
            ] },
            { kind: "category", name: "Text", categorystyle: "text_category", contents: [
                { kind: "block", type: "text" }, { kind: "block", type: "text_join" }, { kind: "block", type: "text_length" }
            ] },
            { kind: "category", name: "Variables", categorystyle: "variable_category", custom: "VARIABLE" },
            { kind: "category", name: "Functions", categorystyle: "procedure_category", custom: "PROCEDURE" },
            { kind: "category", name: "Advanced", colour: colours.advanced, contents: [
                ...advanced("Program & robot", ["program", "robot"]),
                ...advanced("Motors & drive", ["motors", "encoders", "drive"]),
                ...advanced("Servos", ["servos"]), ...advanced("Sensors", ["distance", "colour", "line"]),
                ...advanced("Math & control", ["math-extra"]), ...advanced("Raw hardware", ["hardware"]),
                { kind: "label", text: "Optional hardware" },
                ...specs.filter(spec => spec.priority === "Optional").map(toolboxBlock)
            ] }
        ]
    };
}

export const checklistBlockCount = 169;
