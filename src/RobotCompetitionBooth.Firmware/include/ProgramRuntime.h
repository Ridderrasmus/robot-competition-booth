#pragma once
#include <Arduino.h>
#include <ArduinoJson.h>
#include <functional>

// Pin assignment is supplied by the booth server over authenticated MQTT.
// -1 means the corresponding component is not assembled/enabled.
struct HardwareConfiguration {
    int leftMotorPwm = -1, leftMotorDirection = -1, rightMotorPwm = -1, rightMotorDirection = -1;
    int leftEncoderA = -1, leftEncoderB = -1, rightEncoderA = -1, rightEncoderB = -1;
    int servos[5] = {-1, -1, -1, -1, -1};
    int distanceTrigger = -1, distanceEcho = -1;
    int colourSda = -1, colourScl = -1;
    int lineSensors[5] = {-1, -1, -1, -1, -1};
};

class ProgramRuntime {
public:
    using StatusCallback = std::function<void(const char*, const char*, const char*)>;
    using LightCallback = std::function<void(uint8_t, uint8_t, uint8_t)>;
    using LogCallback = std::function<void(const char*, const String&)>;
    ProgramRuntime(
        HardwareConfiguration& hardware,
        StatusCallback status,
        LightCallback light,
        LogCallback log);
    bool deploy(const String& envelope, String& error);
    bool control(const String& command, String& error);
    void tick();
    bool running() const { return running_; }
    const String& programId() const { return programId_; }

private:
    HardwareConfiguration& hardware_;
    StatusCallback status_;
    LightCallback light_;
    LogCallback log_;
    JsonDocument package_;
    String programId_;
    bool running_ = false, stopRequested_ = false;
    size_t foreverIndex_ = 0;
    unsigned long timers_[3]{};
    float variables_[256]{};
    float motorSpeed_[2]{}, encoderCount_[2]{}, servoAngle_[5]{};
    float wheelDiameterMm_ = 65, trackWidthMm_ = 140;

    bool validateNode(JsonObjectConst node, int depth, String& error) const;
    void runStack(JsonObjectConst node, int budget = 1000);
    void runOne(JsonObjectConst node);
    double number(JsonObjectConst node);
    String text(JsonObjectConst node);
    bool boolean(JsonObjectConst node);
    JsonObjectConst input(JsonObjectConst node, const char* name) const;
    void safeStop();
    void drive(float left, float right);
    static int motorIndex(const char* name);
};
