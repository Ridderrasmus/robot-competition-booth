#include "ProgramRuntime.h"
#include <WiFi.h>
#include <cmath>

namespace {
bool supported(const char* op) {
    static const char* exact[] = {"math_number","text","logic_boolean","logic_compare","logic_operation","logic_negate",
        "math_arithmetic","math_single","math_round","math_random_int","text_join","text_length","variables_get","variables_set",
        "controls_if","controls_repeat_ext","controls_for","controls_flow_statements","prg_on_start","robot_start","prg_forever"};
    for (auto value : exact) if (!strcmp(op, value)) return true;
    static const char* prefixes[] = {"prg_","rbt_","robot_","mot_","enc_","drv_","srv_","dst_","clr_","lin_","com_","con_","mat_","adv_","procedures_"};
    for (auto prefix : prefixes) if (!strncmp(op, prefix, strlen(prefix))) return true;
    return false;
}
float clampSpeed(double value) { return constrain(static_cast<float>(value), -100.0f, 100.0f); }
}

ProgramRuntime::ProgramRuntime(HardwareConfiguration& hardware, StatusCallback status, LightCallback light)
    : hardware_(hardware), status_(std::move(status)), light_(std::move(light)) {}

bool ProgramRuntime::deploy(const String& envelope, String& error) {
    JsonDocument incoming;
    if (deserializeJson(incoming, envelope)) { error = "invalid-envelope"; return false; }
    JsonObjectConst root = incoming.as<JsonObjectConst>();
    JsonObjectConst candidate = root["package"];
    if (root["version"].as<int>() != 1 || candidate["contractVersion"].as<int>() != 1 ||
        !candidate["safety"]["stopAllOutputsOnEnd"].as<bool>() || !candidate["safety"]["stopAllOutputsOnFault"].as<bool>()) {
        error = "unsupported-version"; return false;
    }
    int nodes = 0;
    for (JsonObjectConst entry : candidate["entrypoints"].as<JsonArrayConst>()) {
        if (++nodes > 128 || !validateNode(entry["root"], 0, error)) return false;
    }
    package_.clear();
    package_.set(candidate);
    programId_ = package_["programId"].as<String>();
    status_(root["requestId"] | "", "ready", "");
    return true;
}

bool ProgramRuntime::validateNode(JsonObjectConst node, int depth, String& error) const {
    if (node.isNull() || depth > 128 || !supported(node["opcode"] | "")) { error = "unsupported-opcode"; return false; }
    for (JsonPairConst pair : node["inputs"].as<JsonObjectConst>()) if (!validateNode(pair.value(), depth + 1, error)) return false;
    if (!node["next"].isNull() && !validateNode(node["next"], depth + 1, error)) return false;
    return true;
}

bool ProgramRuntime::control(const String& command, String& error) {
    JsonDocument message;
    if (deserializeJson(message, command) || message["version"].as<int>() != 1) { error = "invalid-envelope"; return false; }
    const char* action = message["action"] | "";
    if (!strcmp(action, "stop")) { stopRequested_ = true; safeStop(); status_(message["requestId"] | "", "stopped", ""); return true; }
    if (strcmp(action, "run") || programId_.isEmpty() || message["programId"].as<String>() != programId_) { error = "program-not-found"; return false; }
    stopRequested_ = false; running_ = true; foreverIndex_ = 0;
    bool hasForeverEntrypoint = false;
    for (JsonObjectConst entry : package_["entrypoints"].as<JsonArrayConst>()) {
        const char* kind = entry["kind"] | "";
        if (!strcmp(kind, "onStart")) runStack(entry["root"]);
        else if (!strcmp(kind, "forever")) hasForeverEntrypoint = true;
    }
    // A program made only of on-start commands is complete once that stack
    // returns. This releases the status light back to its idle connection
    // indicator instead of leaving the runtime permanently marked as running.
    if (running_ && !hasForeverEntrypoint) safeStop();
    status_(message["requestId"] | "", running_ ? "running" : "stopped", "");
    return true;
}

void ProgramRuntime::tick() {
    if (!running_) return;
    JsonArrayConst entries = package_["entrypoints"];
    if (entries.size() == 0) { safeStop(); return; }
    for (size_t checked = 0; checked < entries.size(); ++checked) {
        foreverIndex_ = (foreverIndex_ + 1) % entries.size();
        JsonObjectConst entry = entries[foreverIndex_];
        if (!strcmp(entry["kind"] | "", "forever")) { runStack(input(entry["root"], "DO"), 100); break; }
    }
    if (stopRequested_) safeStop();
}

JsonObjectConst ProgramRuntime::input(JsonObjectConst node, const char* name) const { return node["inputs"][name].as<JsonObjectConst>(); }
int ProgramRuntime::motorIndex(const char* name) { return name && !strcmp(name, "right") ? 1 : 0; }
void ProgramRuntime::safeStop() { motorSpeed_[0] = motorSpeed_[1] = 0; drive(0, 0); running_ = false; stopRequested_ = false; }
void ProgramRuntime::drive(float left, float right) {
    motorSpeed_[0] = clampSpeed(left); motorSpeed_[1] = clampSpeed(right);
    // Disabled (-1) outputs are deliberately inert until hardware configuration exists.
    const int pwm[2] = {hardware_.leftMotorPwm, hardware_.rightMotorPwm};
    for (int i=0;i<2;i++) if (pwm[i] >= 0) analogWrite(pwm[i], static_cast<int>(fabs(motorSpeed_[i]) * 2.55f));
}

void ProgramRuntime::runStack(JsonObjectConst node, int budget) {
    while (!node.isNull() && budget-- > 0 && running_ && !stopRequested_) { if (!node["disabled"].as<bool>()) runOne(node); node = node["next"]; yield(); }
}

void ProgramRuntime::runOne(JsonObjectConst n) {
    const char* op=n["opcode"]|"";
    if (!strcmp(op,"prg_pause_ms") || !strcmp(op,"robot_wait")) delay(constrain(number(input(n,"TIME")),0,60000));
    else if (!strcmp(op,"prg_pause_seconds")) delay(constrain(number(input(n,"TIME"))*1000,0,60000));
    else if (!strcmp(op,"prg_stop")) stopRequested_=true;
    else if (!strcmp(op,"prg_reset_timer")) timers_[0]=millis();
    else if (!strcmp(op,"rbt_set_status_light") || !strcmp(op,"robot_set_light")) { String c=n["fields"]["COLOUR"]|"#000000"; long rgb=strtol(c.c_str()+1,nullptr,16); light_(rgb>>16,(rgb>>8)&255,rgb&255); }
    else if (!strcmp(op,"rbt_clear_status_light")) light_(0,0,0);
    else if (!strcmp(op,"rbt_blink_status_light")) { String c=n["fields"]["COLOUR"]|"#0000ff"; long rgb=strtol(c.c_str()+1,nullptr,16); int times=constrain(number(input(n,"TIMES")),0,50); for(int i=0;i<times;i++){light_(rgb>>16,(rgb>>8)&255,rgb&255);delay(150);light_(0,0,0);delay(150);} }
    else if (!strcmp(op,"mot_run")) { int i=motorIndex(n["fields"]["MOTOR"]); motorSpeed_[i]=clampSpeed(number(input(n,"SPEED"))); drive(motorSpeed_[0],motorSpeed_[1]); }
    else if (!strncmp(op,"mot_run_for_",12)) { int i=motorIndex(n["fields"]["MOTOR"]); motorSpeed_[i]=clampSpeed(number(input(n,"SPEED"))); drive(motorSpeed_[0],motorSpeed_[1]); delay(!strcmp(op,"mot_run_for_ms")?number(input(n,"TIME")):100); motorSpeed_[i]=0; drive(motorSpeed_[0],motorSpeed_[1]); }
    else if (!strcmp(op,"mot_stop" )||!strcmp(op,"mot_stop_mode")) { motorSpeed_[motorIndex(n["fields"]["MOTOR"])]=0; drive(motorSpeed_[0],motorSpeed_[1]); }
    else if (!strcmp(op,"mot_stop_all")) drive(0,0);
    else if (!strcmp(op,"drv_tank")||!strncmp(op,"drv_tank_for_",13)) { drive(number(input(n,"LEFT")),number(input(n,"RIGHT"))); if(strstr(op,"for_")){delay(number(input(n,"TIME")));drive(0,0);} }
    else if (!strncmp(op,"drv_",4)) { float speed=clampSpeed(number(input(n,"SPEED"))); if(!strcmp(op,"drv_backward"))speed=-speed; if(strstr(op,"turn_left"))drive(-speed,speed);else if(strstr(op,"turn_right"))drive(speed,-speed);else drive(speed,speed); }
    else if (!strcmp(op,"srv_set_angle")||!strcmp(op,"srv_center")) { int i=constrain(atoi(n["fields"]["SERVO"]|"1")-1,0,4); servoAngle_[i]=!strcmp(op,"srv_center")?90:constrain(number(input(n,"ANGLE")),0,180); if(hardware_.servos[i]>=0) analogWrite(hardware_.servos[i],map(servoAngle_[i],0,180,26,128)); }
    else if (!strcmp(op,"controls_repeat_ext")) { int count=constrain(number(input(n,"TIMES")),0,10000); while(count--&&running_)runStack(input(n,"DO")); }
    else if (!strcmp(op,"controls_if")) { if(boolean(input(n,"IF0")))runStack(input(n,"DO0"));else runStack(input(n,"ELSE")); }
    else if (!strcmp(op,"variables_set")) { /* IDs are mapped in a future typed variable table; accepted safely now. */ }
    // Configuration, calibration, communications, and unassigned peripheral commands are valid no-ops.
}

double ProgramRuntime::number(JsonObjectConst n) {
    if(n.isNull())return 0; const char* op=n["opcode"]|"";
    if(!strcmp(op,"math_number"))return n["fields"]["NUM"]|0;
    if(!strcmp(op,"prg_timer_millis"))return millis()-timers_[0]; if(!strcmp(op,"prg_timer_seconds"))return (millis()-timers_[0])/1000.0;
    if(!strncmp(op,"enc_",4)){int i=motorIndex(n["fields"]["MOTOR"]);if(!strcmp(op,"enc_speed"))return motorSpeed_[i];if(!strcmp(op,"enc_rotations"))return encoderCount_[i]/360.0;return encoderCount_[i];}
    if(!strcmp(op,"srv_angle"))return servoAngle_[constrain(atoi(n["fields"]["SERVO"]|"1")-1,0,4)];
    if(!strcmp(op,"math_arithmetic")){double a=number(input(n,"A")),b=number(input(n,"B"));String o=n["fields"]["OP"]|"ADD";if(o=="MINUS")return a-b;if(o=="MULTIPLY")return a*b;if(o=="DIVIDE")return b?a/b:0;if(o=="POWER")return pow(a,b);return a+b;}
    // Disabled/unconfigured sensors consistently report neutral values without blocking.
    return 0;
}
String ProgramRuntime::text(JsonObjectConst n){if(n.isNull())return "";const char* op=n["opcode"]|"";if(!strcmp(op,"text"))return n["fields"]["TEXT"]|"";if(!strcmp(op,"rbt_name"))return "RobotBooth-ESP32S3";return String(number(n));}
bool ProgramRuntime::boolean(JsonObjectConst n){if(n.isNull())return false;const char* op=n["opcode"]|"";if(!strcmp(op,"logic_boolean"))return !strcmp(n["fields"]["BOOL"]|"FALSE","TRUE");if(!strcmp(op,"logic_negate"))return !boolean(input(n,"BOOL"));if(!strcmp(op,"rbt_is_connected")||!strcmp(op,"com_dashboard_connected"))return WiFi.isConnected();return number(n)!=0;}
