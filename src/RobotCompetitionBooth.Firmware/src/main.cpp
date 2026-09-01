#include <Arduino.h>
#include <esp32-hal-ledc.h>
#include <esp32-hal-rgb-led.h>
#include <MQTT.h>
#include <NimBLEDevice.h>
#include <WiFi.h>
#include <Wire.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include "BuildConfig.h"
#include "ProgramRuntime.h"

#include <algorithm>
#include <cctype>
#include <cstdarg>
#include <cstring>
#include <vector>

namespace {

constexpr const char* DeviceName = RobotBuildConfig::DeviceName;
constexpr char MqttUsername[] = "robobooth";

// BLE displays passkeys as six digits, adding leading zeroes when necessary.
constexpr uint32_t PairingPasskey = RobotBuildConfig::PairingPasskey;

constexpr char ServiceUuid[] = "8ddf7a40-7520-4e57-9e32-9b6b091c5c8b";
constexpr char StatusCharacteristicUuid[] = "f6eb0c76-11a6-4a5f-a58d-6b55d94ff31a";
constexpr char ProvisioningCharacteristicUuid[] = "0a7c0061-88a4-43cc-a010-faf7595da303";
constexpr char ProvisioningStatusCharacteristicUuid[] = "0a7c0062-88a4-43cc-a010-faf7595da303";

constexpr uint8_t ProvisioningProtocolVersion = 2;
constexpr uint8_t ProvisioningStartCommand = 1;
constexpr uint8_t ProvisioningDataCommand = 2;
constexpr uint8_t ProvisioningCommitCommand = 3;
constexpr size_t MaximumSsidLength = 32;
constexpr size_t MinimumWifiPasswordLength = 8;
constexpr size_t MaximumWifiPasswordLength = 64;
constexpr size_t MaximumMqttHostLength = 63;
constexpr size_t MqttPasswordLength = 64;
constexpr unsigned long InitialWifiConnectionTimeoutMs = 20000;
constexpr unsigned long InitialMqttConnectionTimeoutMs = 15000;
constexpr unsigned long MqttReconnectIntervalMs = 3000;
constexpr unsigned long ColorPublishIntervalMs = 1000;
constexpr unsigned long SensorPublishIntervalMs = 200;
constexpr unsigned long SensorPublishErrorLogIntervalMs = 5000;
constexpr unsigned long LedRefreshIntervalMs = 30;
constexpr uint8_t LedBrightness = 28;
constexpr size_t DiagnosticLogQueueLength = 32;
constexpr size_t MaximumDiagnosticLogMessageLength = 320;
constexpr size_t MaximumDiagnosticLogsPerLoop = 4;
constexpr size_t HardwarePinRoleCount = 22;

struct ProvisioningMessage {
    char ssid[MaximumSsidLength + 1];
    char wifiPassword[MaximumWifiPasswordLength + 1];
    char mqttHost[MaximumMqttHostLength + 1];
    char mqttPassword[MqttPasswordLength + 1];
    uint16_t mqttPort;
};

struct RgbColor {
    uint8_t red;
    uint8_t green;
    uint8_t blue;
};

struct DiagnosticLogMessage {
    uint32_t sequence;
    uint32_t uptimeMs;
    char level[8];
    char source[24];
    char message[MaximumDiagnosticLogMessageLength + 1];
};

NimBLECharacteristic* provisioningStatusCharacteristic = nullptr;
QueueHandle_t provisioningQueue = nullptr;
QueueHandle_t diagnosticLogQueue = nullptr;
WiFiClient wifiClient;
// Deployments need a large receive buffer, but robot publications are small.
// Keeping the write buffer bounded avoids reserving a second 256 KiB heap block.
MQTTClient mqttClient(256 * 1024, 8 * 1024);

char deviceId[32]{};
char colorTopic[128]{};
char sensorTopic[128]{};
char diagnosticLogTopic[128]{};
char statusTopic[128]{};
char programDeployTopic[128]{};
char programControlTopic[128]{};
char programStatusTopic[128]{};
char hardwareConfigurationTopic[128]{};
char hardwareConfigurationStatusTopic[128]{};
char mqttHost[MaximumMqttHostLength + 1]{};
char mqttPassword[MqttPasswordLength + 1]{};
uint16_t mqttPort = 0;

bool mqttConfigured = false;
bool wifiWasConnected = false;
bool initialWifiConnection = false;
bool mqttFailureReported = false;
bool mqttWasConnected = false;
volatile bool bleAuthenticated = false;
unsigned long wifiConnectionStartedAt = 0;
unsigned long mqttConnectionStartedAt = 0;
unsigned long lastMqttConnectAttemptAt = 0;
unsigned long lastColorPublishedAt = 0;
unsigned long lastSensorPublishedAt = 0;
unsigned long lastSensorPublishErrorAt = 0;
unsigned long lastLedRefreshAt = 0;
uint32_t colorSequence = 0;
uint32_t sensorSequence = 0;
uint32_t diagnosticLogSequence = 0;
portMUX_TYPE diagnosticLogSequenceLock = portMUX_INITIALIZER_UNLOCKED;
RgbColor currentColor{255, 0, 0};
bool idleConnectionState = false;
bool idleConnectionStateInitialized = false;
HardwareConfiguration hardwareConfiguration;
ProgramRuntime* programRuntime = nullptr;
uint32_t programStatusSequence = 0;

struct PendingProgramStatus {
    bool available = false;
    char requestId[40]{};
    char state[16]{};
    char errorCode[32]{};
};

PendingProgramStatus pendingProgramStatus;

void logDiagnostic(const char* level, const char* source, const char* format, ...) {
    DiagnosticLogMessage entry{};
    portENTER_CRITICAL(&diagnosticLogSequenceLock);
    entry.sequence = diagnosticLogSequence++;
    portEXIT_CRITICAL(&diagnosticLogSequenceLock);
    entry.uptimeMs = millis();
    snprintf(entry.level, sizeof(entry.level), "%s", level == nullptr ? "info" : level);
    snprintf(entry.source, sizeof(entry.source), "%s", source == nullptr ? "firmware" : source);

    va_list arguments;
    va_start(arguments, format);
    vsnprintf(entry.message, sizeof(entry.message), format, arguments);
    va_end(arguments);

    size_t messageLength = strlen(entry.message);
    while (messageLength > 0 &&
           (entry.message[messageLength - 1] == '\r' || entry.message[messageLength - 1] == '\n')) {
        entry.message[--messageLength] = '\0';
    }
    if (messageLength == 0) {
        return;
    }

    Serial.printf("[%s] %s\n", entry.source, entry.message);
    if (diagnosticLogQueue == nullptr) {
        return;
    }

    if (xQueueSend(diagnosticLogQueue, &entry, 0) != pdPASS) {
        DiagnosticLogMessage discarded{};
        xQueueReceive(diagnosticLogQueue, &discarded, 0);
        xQueueSend(diagnosticLogQueue, &entry, 0);
    }
}

void flushDiagnosticLogs() {
    if (!mqttClient.connected() || diagnosticLogQueue == nullptr || diagnosticLogTopic[0] == '\0') {
        return;
    }

    for (size_t published = 0; published < MaximumDiagnosticLogsPerLoop; ++published) {
        DiagnosticLogMessage entry{};
        if (xQueuePeek(diagnosticLogQueue, &entry, 0) != pdPASS) {
            return;
        }

        JsonDocument document;
        document["version"] = 1;
        document["sequence"] = entry.sequence;
        document["uptimeMs"] = entry.uptimeMs;
        document["level"] = entry.level;
        document["source"] = entry.source;
        document["message"] = entry.message;
        char payload[768]{};
        const size_t payloadLength = serializeJson(document, payload, sizeof(payload));
        if (payloadLength == 0 || payloadLength >= sizeof(payload) ||
            !mqttClient.publish(diagnosticLogTopic, payload, false, 0)) {
            return;
        }

        xQueueReceive(diagnosticLogQueue, &entry, 0);
    }
}

void publishProgramStatus(const char* requestId, const char* state, const char* errorCode) {
    if (!mqttClient.connected()) return;
    char payload[768]{};
    snprintf(payload, sizeof(payload),
        "{\"version\":1,\"sequence\":%lu,\"requestId\":\"%s\",\"state\":\"%s\",\"programId\":\"%s\"%s%s%s}",
        static_cast<unsigned long>(programStatusSequence++), requestId, state,
        programRuntime == nullptr ? "" : programRuntime->programId().c_str(),
        errorCode && errorCode[0] ? ",\"errorCode\":\"" : "",
        errorCode && errorCode[0] ? errorCode : "",
        errorCode && errorCode[0] ? "\"" : "");
    mqttClient.publish(programStatusTopic, payload, true, 1);
}

void queueProgramStatus(const char* requestId, const char* state, const char* errorCode) {
    snprintf(pendingProgramStatus.requestId, sizeof(pendingProgramStatus.requestId), "%s", requestId == nullptr ? "" : requestId);
    snprintf(pendingProgramStatus.state, sizeof(pendingProgramStatus.state), "%s", state == nullptr ? "" : state);
    snprintf(pendingProgramStatus.errorCode, sizeof(pendingProgramStatus.errorCode), "%s", errorCode == nullptr ? "" : errorCode);
    pendingProgramStatus.available = true;
}

void flushProgramStatus() {
    if (!pendingProgramStatus.available || !mqttClient.connected()) return;
    PendingProgramStatus status = pendingProgramStatus;
    pendingProgramStatus.available = false;
    publishProgramStatus(status.requestId, status.state, status.errorCode);
}

bool isHardwareOutputPin(int pin) {
    return ((pin >= 1 && pin <= 18) && pin != 3) || pin == 21 ||
        (pin >= 38 && pin <= 44) || pin == 47;
}

bool isHardwareInputPin(int pin) { return isHardwareOutputPin(pin); }

bool isHardwareAnalogPin(int pin) {
    return pin >= 1 && pin <= 10 && pin != 3;
}

bool hardwarePairIsComplete(const int* pins, size_t first, size_t second) {
    return (pins[first] >= 0) == (pins[second] >= 0);
}

bool hardwareGroupIsComplete(const int* pins, size_t first, size_t count) {
    size_t configured = 0;
    for (size_t index = 0; index < count; ++index) {
        if (pins[first + index] >= 0) ++configured;
    }
    return configured == 0 || configured == count;
}

void publishHardwareConfigurationStatus(
    const String& requestId,
    const char* state,
    const char* errorCode = nullptr) {
    if (!mqttClient.connected()) return;
    JsonDocument document;
    document["version"] = 1;
    document["requestId"] = requestId.isEmpty() ? "unknown" : requestId;
    document["state"] = state;
    if (errorCode != nullptr && errorCode[0] != '\0') {
        document["errorCode"] = errorCode;
    }
    char payload[256]{};
    const size_t length = serializeJson(document, payload, sizeof(payload));
    if (length > 0 && length < sizeof(payload)) {
        mqttClient.publish(hardwareConfigurationStatusTopic, payload, true, 1);
    }
}

void applyHardwareConfiguration(const HardwareConfiguration& configuration) {
    if (programRuntime != nullptr && programRuntime->running()) {
        String ignored;
        programRuntime->control(
            "{\"version\":1,\"requestId\":\"hardware-config\",\"action\":\"stop\"}",
            ignored);
    }

    const int oldPwmPins[] = {
        hardwareConfiguration.leftMotorPwm,
        hardwareConfiguration.rightMotorPwm,
        hardwareConfiguration.servos[0],
        hardwareConfiguration.servos[1],
        hardwareConfiguration.servos[2],
        hardwareConfiguration.servos[3],
        hardwareConfiguration.servos[4]
    };
    for (size_t index = 0; index < 7; ++index) {
        if (oldPwmPins[index] >= 0) {
            ledcWrite(static_cast<uint8_t>(index), 0);
            ledcDetachPin(oldPwmPins[index]);
            pinMode(oldPwmPins[index], INPUT);
        }
    }

    const int oldDigitalOutputs[] = {
        hardwareConfiguration.leftMotorDirection,
        hardwareConfiguration.rightMotorDirection,
        hardwareConfiguration.distanceTrigger
    };
    for (int pin : oldDigitalOutputs) {
        if (pin >= 0) {
            digitalWrite(pin, LOW);
            pinMode(pin, INPUT);
        }
    }
    if (hardwareConfiguration.colourSda >= 0) {
        Wire.end();
    }

    hardwareConfiguration = configuration;

    const int motorPwm[] = {configuration.leftMotorPwm, configuration.rightMotorPwm};
    const int motorDirection[] = {configuration.leftMotorDirection, configuration.rightMotorDirection};
    for (size_t index = 0; index < 2; ++index) {
        if (motorPwm[index] < 0) continue;
        pinMode(motorPwm[index], OUTPUT);
        ledcSetup(static_cast<uint8_t>(index), 1000, 8);
        ledcAttachPin(motorPwm[index], static_cast<uint8_t>(index));
        ledcWrite(static_cast<uint8_t>(index), 0);
        pinMode(motorDirection[index], OUTPUT);
        digitalWrite(motorDirection[index], LOW);
    }

    const int encoderPins[] = {
        configuration.leftEncoderA,
        configuration.leftEncoderB,
        configuration.rightEncoderA,
        configuration.rightEncoderB
    };
    for (int pin : encoderPins) {
        if (pin >= 0) pinMode(pin, INPUT_PULLUP);
    }

    for (size_t index = 0; index < 5; ++index) {
        const int pin = configuration.servos[index];
        if (pin < 0) continue;
        pinMode(pin, OUTPUT);
        const uint8_t channel = static_cast<uint8_t>(index + 2);
        ledcSetup(channel, 50, 12);
        ledcAttachPin(pin, channel);
        ledcWrite(channel, 0);
    }

    if (configuration.distanceTrigger >= 0) {
        pinMode(configuration.distanceTrigger, OUTPUT);
        digitalWrite(configuration.distanceTrigger, LOW);
        pinMode(configuration.distanceEcho, INPUT);
    }
    if (configuration.colourSda >= 0) {
        Wire.begin(configuration.colourSda, configuration.colourScl);
    }
    for (int pin : configuration.lineSensors) {
        if (pin >= 0) pinMode(pin, INPUT);
    }

    size_t configuredPins = 0;
    const int allPins[] = {
        configuration.leftMotorPwm, configuration.leftMotorDirection,
        configuration.rightMotorPwm, configuration.rightMotorDirection,
        configuration.leftEncoderA, configuration.leftEncoderB,
        configuration.rightEncoderA, configuration.rightEncoderB,
        configuration.servos[0], configuration.servos[1], configuration.servos[2],
        configuration.servos[3], configuration.servos[4],
        configuration.distanceTrigger, configuration.distanceEcho,
        configuration.colourSda, configuration.colourScl,
        configuration.lineSensors[0], configuration.lineSensors[1],
        configuration.lineSensors[2], configuration.lineSensors[3],
        configuration.lineSensors[4]
    };
    for (int pin : allPins) {
        if (pin >= 0) ++configuredPins;
    }
    logDiagnostic(
        "info",
        "hardware",
        "Applied server hardware configuration with %u assigned pin(s).",
        static_cast<unsigned>(configuredPins));
}

bool decodeAndApplyHardwareConfiguration(
    const String& payload,
    String& requestId,
    String& error) {
    static const char* pinKeys[HardwarePinRoleCount] = {
        "leftMotorPwm", "leftMotorDirection", "rightMotorPwm", "rightMotorDirection",
        "leftEncoderA", "leftEncoderB", "rightEncoderA", "rightEncoderB",
        "servo1", "servo2", "servo3", "servo4", "servo5",
        "distanceTrigger", "distanceEcho", "colourSda", "colourScl",
        "lineLeftOuter", "lineLeft", "lineCentre", "lineRight", "lineRightOuter"
    };

    JsonDocument document;
    const auto parseResult = deserializeJson(document, payload);
    if (parseResult || document["version"].as<int>() != 1) {
        error = "invalid-envelope";
        return false;
    }

    requestId = document["requestId"].as<String>();
    if (requestId.length() == 0 || requestId.length() > 64) {
        error = "invalid-request-id";
        return false;
    }
    for (size_t index = 0; index < requestId.length(); ++index) {
        const char character = requestId[index];
        if (!isalnum(static_cast<unsigned char>(character)) && character != '-') {
            error = "invalid-request-id";
            return false;
        }
    }

    JsonObjectConst pinObject = document["pins"].as<JsonObjectConst>();
    if (pinObject.isNull()) {
        error = "missing-pins";
        return false;
    }

    int pins[HardwarePinRoleCount];
    std::fill_n(pins, HardwarePinRoleCount, -1);
    for (JsonPairConst pair : pinObject) {
        size_t role = HardwarePinRoleCount;
        for (size_t candidate = 0; candidate < HardwarePinRoleCount; ++candidate) {
            if (!strcmp(pair.key().c_str(), pinKeys[candidate])) {
                role = candidate;
                break;
            }
        }
        if (role == HardwarePinRoleCount || !pair.value().is<int>()) {
            error = "unsupported-pin-role";
            return false;
        }

        const int pin = pair.value().as<int>();
        const bool outputRole = role <= 3 || (role >= 8 && role <= 13) || role == 15 || role == 16;
        const bool analogRole = role >= 17;
        const bool capable = outputRole
            ? isHardwareOutputPin(pin)
            : analogRole
                ? isHardwareAnalogPin(pin)
                : isHardwareInputPin(pin);
        if (!capable) {
            error = "unsafe-pin";
            return false;
        }
        for (size_t previous = 0; previous < HardwarePinRoleCount; ++previous) {
            if (pins[previous] == pin) {
                error = "duplicate-pin";
                return false;
            }
        }
        pins[role] = pin;
    }

    if (!hardwarePairIsComplete(pins, 0, 1) || !hardwarePairIsComplete(pins, 2, 3) ||
        !hardwarePairIsComplete(pins, 4, 5) || !hardwarePairIsComplete(pins, 6, 7) ||
        !hardwarePairIsComplete(pins, 13, 14) || !hardwarePairIsComplete(pins, 15, 16) ||
        !hardwareGroupIsComplete(pins, 17, 5)) {
        error = "incomplete-component";
        return false;
    }

    HardwareConfiguration configuration{};
    configuration.leftMotorPwm = pins[0];
    configuration.leftMotorDirection = pins[1];
    configuration.rightMotorPwm = pins[2];
    configuration.rightMotorDirection = pins[3];
    configuration.leftEncoderA = pins[4];
    configuration.leftEncoderB = pins[5];
    configuration.rightEncoderA = pins[6];
    configuration.rightEncoderB = pins[7];
    for (size_t index = 0; index < 5; ++index) {
        configuration.servos[index] = pins[8 + index];
        configuration.lineSensors[index] = pins[17 + index];
    }
    configuration.distanceTrigger = pins[13];
    configuration.distanceEcho = pins[14];
    configuration.colourSda = pins[15];
    configuration.colourScl = pins[16];
    applyHardwareConfiguration(configuration);
    return true;
}

void receiveMqttMessage(String& topic, String& payload) {
    if (programRuntime == nullptr) return;
    logDiagnostic("debug", "mqtt", "Received %s (%u bytes).", topic.c_str(), static_cast<unsigned>(payload.length()));
    String error;
    bool accepted = false;
    if (topic == hardwareConfigurationTopic) {
        String requestId;
        accepted = decodeAndApplyHardwareConfiguration(payload, requestId, error);
        publishHardwareConfigurationStatus(
            requestId,
            accepted ? "applied" : "rejected",
            accepted ? nullptr : error.c_str());
    } else if (topic == programDeployTopic) accepted = programRuntime->deploy(payload, error);
    else if (topic == programControlTopic) accepted = programRuntime->control(payload, error);
    if (accepted) {
        logDiagnostic("info", "mqtt", "Accepted %s.", topic.c_str());
    } else if (!error.isEmpty()) {
        logDiagnostic("warn", "mqtt", "Rejected %s: %s.", topic.c_str(), error.c_str());
        if (topic != hardwareConfigurationTopic) {
            queueProgramStatus("", "failed", error.c_str());
        }
    }
}

void setProvisioningStatus(const char* status) {
    if (provisioningStatusCharacteristic != nullptr) {
        provisioningStatusCharacteristic->setValue(status);
    }
}

void showStatus(uint8_t red, uint8_t green, uint8_t blue) {
    neopixelWrite(RGB_BUILTIN, red, green, blue);
}

bool isAsciiHex(uint8_t character) {
    return (character >= '0' && character <= '9') ||
        (character >= 'a' && character <= 'f') ||
        (character >= 'A' && character <= 'F');
}

void updateIdleStatusLight() {
    const unsigned long now = millis();
    if (now - lastLedRefreshAt < LedRefreshIntervalMs) {
        return;
    }

    lastLedRefreshAt = now;
    const bool connected = bleAuthenticated || mqttClient.connected();
    if (!idleConnectionStateInitialized || connected != idleConnectionState) {
        idleConnectionState = connected;
        idleConnectionStateInitialized = true;
        logDiagnostic(
            "debug",
            "status-light",
            "Idle status light: %s (BLE authenticated: %s, MQTT connected: %s).",
            connected ? "green" : "red",
            bleAuthenticated ? "yes" : "no",
            mqttClient.connected() ? "yes" : "no");
    }
    currentColor = connected ? RgbColor{0, 255, 0} : RgbColor{255, 0, 0};
    showStatus(
        static_cast<uint8_t>((static_cast<uint16_t>(currentColor.red) * LedBrightness) / 255),
        static_cast<uint8_t>((static_cast<uint16_t>(currentColor.green) * LedBrightness) / 255),
        static_cast<uint8_t>((static_cast<uint16_t>(currentColor.blue) * LedBrightness) / 255));
}

class ServerCallbacks final : public NimBLEServerCallbacks {
    void onConnect(NimBLEServer* server, NimBLEConnInfo& connection) override {
        bleAuthenticated = false;
        logDiagnostic(
            "info",
            "bluetooth",
            "BLE client connected: %s",
            connection.getAddress().toString().c_str());
        Serial.printf("Pairing passkey: %06lu\n", static_cast<unsigned long>(PairingPasskey));

        int returnCode = 0;
        if (!NimBLEDevice::startSecurity(connection.getConnHandle(), &returnCode)) {
            logDiagnostic("error", "bluetooth", "Could not start BLE security (code %d); disconnecting.", returnCode);
            server->disconnect(connection.getConnHandle());
        }
    }

    void onDisconnect(
        NimBLEServer*,
        NimBLEConnInfo& connection,
        int reason) override {
        bleAuthenticated = false;
        logDiagnostic(
            "info",
            "bluetooth",
            "BLE client disconnected: %s (reason %d)",
            connection.getAddress().toString().c_str(),
            reason);
    }

    uint32_t onPassKeyDisplay() override {
        Serial.printf("Enter passkey %06lu on the pairing device.\n", static_cast<unsigned long>(PairingPasskey));
        return PairingPasskey;
    }

    void onAuthenticationComplete(NimBLEConnInfo& connection) override {
        if (!connection.isEncrypted() || !connection.isAuthenticated()) {
            bleAuthenticated = false;
            logDiagnostic("error", "bluetooth", "BLE authentication failed; disconnecting client.");
            NimBLEDevice::getServer()->disconnect(connection.getConnHandle());
            return;
        }

        bleAuthenticated = true;

        logDiagnostic(
            "info",
            "bluetooth",
            "BLE pairing succeeded: %s",
            connection.getAddress().toString().c_str());
    }
};

ServerCallbacks serverCallbacks;

class ProvisioningCallbacks final : public NimBLECharacteristicCallbacks {
    size_t expectedSsidLength = 0;
    size_t expectedWifiPasswordLength = 0;
    size_t expectedMqttHostLength = 0;
    size_t expectedMqttPasswordLength = 0;
    uint16_t expectedMqttPort = 0;
    std::vector<uint8_t> payload;

    void resetPayload() {
        std::fill(payload.begin(), payload.end(), 0);
        payload.clear();
        expectedSsidLength = 0;
        expectedWifiPasswordLength = 0;
        expectedMqttHostLength = 0;
        expectedMqttPasswordLength = 0;
        expectedMqttPort = 0;
    }

    void rejectPayload() {
        resetPayload();
        setProvisioningStatus("invalid");
    }

    void startPayload(const uint8_t* data, size_t length) {
        if (length != 8) {
            rejectPayload();
            return;
        }

        expectedSsidLength = data[2];
        expectedWifiPasswordLength = data[3];
        expectedMqttHostLength = data[4];
        expectedMqttPasswordLength = data[5];
        expectedMqttPort = static_cast<uint16_t>(data[6]) |
            (static_cast<uint16_t>(data[7]) << 8);

        if (expectedSsidLength == 0 ||
            expectedSsidLength > MaximumSsidLength ||
            (expectedWifiPasswordLength != 0 &&
             expectedWifiPasswordLength < MinimumWifiPasswordLength) ||
            expectedWifiPasswordLength > MaximumWifiPasswordLength ||
            expectedMqttHostLength == 0 ||
            expectedMqttHostLength > MaximumMqttHostLength ||
            expectedMqttPasswordLength != MqttPasswordLength ||
            expectedMqttPort == 0) {
            rejectPayload();
            return;
        }

        payload.clear();
        payload.reserve(
            expectedSsidLength +
            expectedWifiPasswordLength +
            expectedMqttHostLength +
            expectedMqttPasswordLength);
        setProvisioningStatus("receiving");
    }

    void appendPayload(const uint8_t* data, size_t length) {
        if (length <= 4 || expectedSsidLength == 0) {
            rejectPayload();
            return;
        }

        const size_t offset = static_cast<size_t>(data[2]) |
            (static_cast<size_t>(data[3]) << 8);
        const size_t chunkLength = length - 4;
        const size_t expectedLength =
            expectedSsidLength +
            expectedWifiPasswordLength +
            expectedMqttHostLength +
            expectedMqttPasswordLength;
        if (offset != payload.size() || payload.size() + chunkLength > expectedLength) {
            rejectPayload();
            return;
        }

        payload.insert(payload.end(), data + 4, data + length);
    }

    void commitPayload(size_t length) {
        const size_t expectedLength =
            expectedSsidLength +
            expectedWifiPasswordLength +
            expectedMqttHostLength +
            expectedMqttPasswordLength;
        if (length != 2 || expectedSsidLength == 0 || payload.size() != expectedLength ||
            std::find(payload.begin(), payload.end(), 0) != payload.end() ||
            provisioningQueue == nullptr) {
            rejectPayload();
            return;
        }

        const auto wifiPasswordStart = payload.begin() + expectedSsidLength;
        const auto mqttHostStart = wifiPasswordStart + expectedWifiPasswordLength;
        const auto mqttPasswordStart = mqttHostStart + expectedMqttHostLength;

        const bool invalidRawWifiKey = expectedWifiPasswordLength == MaximumWifiPasswordLength &&
            std::any_of(wifiPasswordStart, mqttHostStart, [](uint8_t character) {
                return !isAsciiHex(character);
            });
        const bool invalidMqttHost = std::any_of(
            mqttHostStart,
            mqttPasswordStart,
            [](uint8_t character) {
                return character < 33 || character > 126;
            });
        const bool invalidMqttPassword = std::any_of(
            mqttPasswordStart,
            payload.end(),
            [](uint8_t character) {
                return !isAsciiHex(character);
            });
        if (invalidRawWifiKey || invalidMqttHost || invalidMqttPassword) {
            rejectPayload();
            return;
        }

        ProvisioningMessage settings{};
        std::memcpy(settings.ssid, payload.data(), expectedSsidLength);
        std::memcpy(
            settings.wifiPassword,
            payload.data() + expectedSsidLength,
            expectedWifiPasswordLength);
        std::memcpy(
            settings.mqttHost,
            payload.data() + expectedSsidLength + expectedWifiPasswordLength,
            expectedMqttHostLength);
        std::memcpy(
            settings.mqttPassword,
            payload.data() + expectedSsidLength + expectedWifiPasswordLength + expectedMqttHostLength,
            expectedMqttPasswordLength);
        settings.mqttPort = expectedMqttPort;

        if (xQueueOverwrite(provisioningQueue, &settings) != pdPASS) {
            std::memset(&settings, 0, sizeof(settings));
            rejectPayload();
            return;
        }

        std::memset(&settings, 0, sizeof(settings));
        resetPayload();
        setProvisioningStatus("queued");
    }

    void onWrite(NimBLECharacteristic* characteristic, NimBLEConnInfo&) override {
        const NimBLEAttValue value = characteristic->getValue();
        const uint8_t* data = value.data();
        const size_t length = value.size();

        if (length < 2 || data[0] != ProvisioningProtocolVersion) {
            rejectPayload();
        } else if (data[1] == ProvisioningStartCommand) {
            resetPayload();
            startPayload(data, length);
        } else if (data[1] == ProvisioningDataCommand) {
            appendPayload(data, length);
        } else if (data[1] == ProvisioningCommitCommand) {
            commitPayload(length);
        } else {
            rejectPayload();
        }

        // The characteristic is write-only, but also clear its retained value so
        // credential fragments do not remain in the BLE attribute table.
        characteristic->setValue("");
    }
};

ProvisioningCallbacks provisioningCallbacks;

void publishColor(bool force = false) {
    if (!mqttClient.connected()) {
        return;
    }

    const unsigned long now = millis();
    if (!force && now - lastColorPublishedAt < ColorPublishIntervalMs) {
        return;
    }

    lastColorPublishedAt = now;
    char payload[160]{};
    snprintf(
        payload,
        sizeof(payload),
        "{\"name\":\"%s\",\"rgb\":\"#%02X%02X%02X\",\"sequence\":%lu}",
        DeviceName,
        currentColor.red,
        currentColor.green,
        currentColor.blue,
        static_cast<unsigned long>(colorSequence++));

    if (!mqttClient.publish(colorTopic, payload, true, 1)) {
        logDiagnostic("warn", "telemetry", "Could not publish the current colour over MQTT.");
    }
}

uint32_t triangleValue(unsigned long now, uint32_t periodMs, uint32_t maximum) {
    const uint32_t position = now % periodMs;
    const uint32_t halfPeriod = periodMs / 2;
    const uint32_t distanceFromEdge = position <= halfPeriod
        ? position
        : periodMs - position;
    return (static_cast<uint64_t>(distanceFromEdge) * maximum) / halfPeriod;
}

void publishSyntheticSensorSnapshot(bool force = false) {
    // User programs read the in-memory hardware cache. Live UI announcements are
    // deliberately silenced during execution to keep the first runtime predictable.
    if (!mqttClient.connected() || (programRuntime != nullptr && programRuntime->running())) {
        return;
    }

    const unsigned long now = millis();
    if (!force && now - lastSensorPublishedAt < SensorPublishIntervalMs) {
        return;
    }
    lastSensorPublishedAt = now;

    const uint32_t distanceMillimetres = 120 + triangleValue(now, 7000, 1080);
    const uint32_t lightPercent = 10 + triangleValue(now + 900, 6000, 85);

    const uint32_t red = static_cast<uint32_t>(currentColor.red) * 257;
    const uint32_t green = static_cast<uint32_t>(currentColor.green) * 257;
    const uint32_t blue = static_cast<uint32_t>(currentColor.blue) * 257;
    const uint32_t clear = (red + green + blue) / 3;
    constexpr const char* DetectedColors[] = {
        "red", "yellow", "green", "cyan", "blue", "magenta", "white", "black"};
    const char* detectedColor = DetectedColors[(now / 2500) % 8];

    const int32_t linePosition = static_cast<int32_t>(triangleValue(now, 6000, 200)) - 100;
    constexpr int32_t LineChannelPositions[] = {-100, -50, 0, 50, 100};
    uint32_t lineNormalized[5]{};
    uint32_t lineRaw[5]{};
    char linePattern[6]{};
    for (size_t index = 0; index < 5; ++index) {
        const uint32_t distance = static_cast<uint32_t>(
            std::abs(linePosition - LineChannelPositions[index]));
        lineNormalized[index] = distance >= 50 ? 5 : 100 - (distance * 95) / 50;
        lineRaw[index] = (lineNormalized[index] * 4095) / 100;
        linePattern[index] = lineNormalized[index] >= 60 ? '1' : '0';
    }
    linePattern[5] = '\0';

    const int64_t leftCount = static_cast<int64_t>(now / 20);
    const int64_t rightCount = static_cast<int64_t>(now / 22);
    const int64_t leftAngle = leftCount * 6;
    const int64_t rightAngle = rightCount * 6;
    const int32_t leftSpeed = static_cast<int32_t>(triangleValue(now, 8000, 200)) - 100;
    const int32_t rightSpeed = static_cast<int32_t>(triangleValue(now + 1200, 8000, 200)) - 100;

    uint32_t servoAngles[5]{};
    for (size_t index = 0; index < 5; ++index) {
        servoAngles[index] = triangleValue(now + static_cast<unsigned long>(index * 700), 5000, 180);
    }

    char payload[1536]{};
    const int payloadLength = snprintf(
        payload,
        sizeof(payload),
        "{\"version\":1,\"sequence\":%lu,\"uptimeMs\":%lu,\"mode\":\"idle\","
        "\"distance\":{\"valid\":true,\"millimetres\":%lu},"
        "\"colour\":{\"valid\":true,\"red\":%lu,\"green\":%lu,\"blue\":%lu,"
        "\"clear\":%lu,\"detected\":\"%s\",\"lightPercent\":%lu},"
        "\"line\":{\"valid\":true,\"raw\":[%lu,%lu,%lu,%lu,%lu],"
        "\"normalized\":[%lu,%lu,%lu,%lu,%lu],\"pattern\":\"%s\",\"position\":%ld},"
        "\"motors\":{\"left\":{\"count\":%lld,\"angleDegrees\":%lld,\"rotations\":%.3f,"
        "\"speedPercent\":%ld},\"right\":{\"count\":%lld,\"angleDegrees\":%lld,"
        "\"rotations\":%.3f,\"speedPercent\":%ld}},"
        "\"servos\":{\"angles\":[%lu,%lu,%lu,%lu,%lu]}}",
        static_cast<unsigned long>(sensorSequence++),
        now,
        static_cast<unsigned long>(distanceMillimetres),
        static_cast<unsigned long>(red),
        static_cast<unsigned long>(green),
        static_cast<unsigned long>(blue),
        static_cast<unsigned long>(clear),
        detectedColor,
        static_cast<unsigned long>(lightPercent),
        static_cast<unsigned long>(lineRaw[0]),
        static_cast<unsigned long>(lineRaw[1]),
        static_cast<unsigned long>(lineRaw[2]),
        static_cast<unsigned long>(lineRaw[3]),
        static_cast<unsigned long>(lineRaw[4]),
        static_cast<unsigned long>(lineNormalized[0]),
        static_cast<unsigned long>(lineNormalized[1]),
        static_cast<unsigned long>(lineNormalized[2]),
        static_cast<unsigned long>(lineNormalized[3]),
        static_cast<unsigned long>(lineNormalized[4]),
        linePattern,
        static_cast<long>(linePosition),
        static_cast<long long>(leftCount),
        static_cast<long long>(leftAngle),
        static_cast<double>(leftCount) / 60.0,
        static_cast<long>(leftSpeed),
        static_cast<long long>(rightCount),
        static_cast<long long>(rightAngle),
        static_cast<double>(rightCount) / 60.0,
        static_cast<long>(rightSpeed),
        static_cast<unsigned long>(servoAngles[0]),
        static_cast<unsigned long>(servoAngles[1]),
        static_cast<unsigned long>(servoAngles[2]),
        static_cast<unsigned long>(servoAngles[3]),
        static_cast<unsigned long>(servoAngles[4]));

    if (payloadLength <= 0 || static_cast<size_t>(payloadLength) >= sizeof(payload)) {
        if (now - lastSensorPublishErrorAt >= SensorPublishErrorLogIntervalMs) {
            lastSensorPublishErrorAt = now;
            logDiagnostic("error", "telemetry", "Synthetic sensor payload exceeded its buffer.");
        }
        return;
    }

    if (!mqttClient.publish(sensorTopic, payload, false, 0) &&
        now - lastSensorPublishErrorAt >= SensorPublishErrorLogIntervalMs) {
        lastSensorPublishErrorAt = now;
        logDiagnostic("warn", "telemetry", "Could not publish synthetic sensor telemetry over MQTT.");
    }
}

void configureMqttClient() {
    mqttClient.begin(mqttHost, mqttPort, wifiClient);
    // The booth link is long-lived. Five seconds tolerates brief Windows/Wi-Fi
    // scheduling stalls without turning harmless latency into a reconnect.
    mqttClient.setOptions(30, true, 5000);
    mqttClient.setWill(statusTopic, "offline", true, 1);
    mqttConnectionStartedAt = millis();
    lastMqttConnectAttemptAt = 0;
    mqttFailureReported = false;
    setProvisioningStatus("mqtt-connecting");
}

void tryConnectMqtt() {
    if (!mqttConfigured || WiFi.status() != WL_CONNECTED || mqttClient.connected()) {
        return;
    }

    const unsigned long now = millis();
    if (lastMqttConnectAttemptAt != 0 &&
        now - lastMqttConnectAttemptAt < MqttReconnectIntervalMs) {
        return;
    }

    lastMqttConnectAttemptAt = now;
    if (mqttClient.connect(deviceId, MqttUsername, mqttPassword)) {
        mqttWasConnected = true;
        mqttFailureReported = false;
        setProvisioningStatus("mqtt-connected");
        mqttClient.publish(statusTopic, "online", true, 1);
        mqttClient.subscribe(programDeployTopic, 1);
        mqttClient.subscribe(programControlTopic, 1);
        mqttClient.subscribe(hardwareConfigurationTopic, 1);
        publishColor(true);
        publishSyntheticSensorSnapshot(true);
        logDiagnostic("info", "mqtt", "Connected to the embedded MQTT broker.");
        return;
    }

    if (!mqttFailureReported && now - mqttConnectionStartedAt >= InitialMqttConnectionTimeoutMs) {
        mqttFailureReported = true;
        setProvisioningStatus("mqtt-failed");
        logDiagnostic("warn", "mqtt", "Could not connect to the embedded MQTT broker; retrying in the background.");
    }
}

void startPendingProvisioning() {
    if (provisioningQueue == nullptr) {
        return;
    }

    ProvisioningMessage settings{};
    if (xQueueReceive(provisioningQueue, &settings, 0) != pdPASS) {
        return;
    }

    const bool settingsAlreadyActive =
        mqttConfigured &&
        WiFi.status() == WL_CONNECTED &&
        mqttClient.connected() &&
        mqttPort == settings.mqttPort &&
        strcmp(mqttHost, settings.mqttHost) == 0 &&
        strcmp(mqttPassword, settings.mqttPassword) == 0 &&
        WiFi.SSID() == settings.ssid;
    if (settingsAlreadyActive) {
        std::memset(&settings, 0, sizeof(settings));
        setProvisioningStatus("mqtt-connected");
        logDiagnostic("info", "provisioning", "Received the already-active network settings; keeping the existing MQTT connection.");
        return;
    }

    if (mqttClient.connected()) {
        mqttClient.publish(statusTopic, "offline", true, 1);
        mqttClient.disconnect();
    }
    mqttWasConnected = false;

    std::memset(mqttHost, 0, sizeof(mqttHost));
    std::memset(mqttPassword, 0, sizeof(mqttPassword));
    std::memcpy(mqttHost, settings.mqttHost, sizeof(mqttHost));
    std::memcpy(mqttPassword, settings.mqttPassword, sizeof(mqttPassword));
    mqttPort = settings.mqttPort;
    mqttConfigured = true;
    wifiWasConnected = false;

    WiFi.disconnect(false, true);
    if (settings.wifiPassword[0] == '\0')
    {
        WiFi.begin(settings.ssid);
    }
    else
    {
        WiFi.begin(settings.ssid, settings.wifiPassword);
    }
    std::memset(&settings, 0, sizeof(settings));

    initialWifiConnection = true;
    wifiConnectionStartedAt = millis();
    setProvisioningStatus("wifi-connecting");
    logDiagnostic("info", "provisioning", "Received Wi-Fi and MQTT settings over authenticated BLE; connecting.");
}

void updateNetworkConnections() {
    const bool wifiConnected = WiFi.status() == WL_CONNECTED;
    if (!wifiConnected) {
        if (wifiWasConnected) {
            wifiWasConnected = false;
            setProvisioningStatus("wifi-connecting");
            logDiagnostic("warn", "wifi", "Wi-Fi connection was lost; waiting for automatic reconnection.");
        }

        if (initialWifiConnection &&
            millis() - wifiConnectionStartedAt >= InitialWifiConnectionTimeoutMs) {
            initialWifiConnection = false;
            mqttConfigured = false;
            mqttPort = 0;
            std::memset(mqttHost, 0, sizeof(mqttHost));
            std::memset(mqttPassword, 0, sizeof(mqttPassword));
            WiFi.disconnect(false, true);
            setProvisioningStatus("wifi-failed");
            logDiagnostic("error", "wifi", "Wi-Fi connection failed; provisioned settings were discarded.");
        }

        return;
    }

    if (!wifiWasConnected) {
        wifiWasConnected = true;
        initialWifiConnection = false;
        configureMqttClient();
        logDiagnostic("info", "wifi", "Wi-Fi connection established; connecting to MQTT.");
    }

    if (mqttClient.connected() && !mqttClient.loop()) {
        logDiagnostic(
            "warn",
            "mqtt",
            "MQTT loop ended the connection (error %d, return code %d); reconnecting.",
            static_cast<int>(mqttClient.lastError()),
            static_cast<int>(mqttClient.returnCode()));
    }
    if (mqttWasConnected && !mqttClient.connected()) {
        mqttWasConnected = false;
        lastMqttConnectAttemptAt = 0;
    }
    flushProgramStatus();
    tryConnectMqtt();
    flushDiagnosticLogs();
    publishColor();
    publishSyntheticSensorSnapshot();
}

} // namespace

void setup() {
    showStatus(LedBrightness, 0, 0);

    Serial.begin(115200);
    delay(500);

    Serial.println();
    diagnosticLogQueue = xQueueCreate(DiagnosticLogQueueLength, sizeof(DiagnosticLogMessage));
    logDiagnostic("info", "startup", "Starting Robot Competition Booth firmware...");
    logDiagnostic("info", "startup", "Detected PSRAM: %u bytes", ESP.getPsramSize());
    if (diagnosticLogQueue == nullptr) {
        Serial.println("Diagnostic telemetry queue is unavailable; continuing without remote logs.");
    }

    const uint64_t chipId = ESP.getEfuseMac() & 0xFFFFFFFFFFFFULL;
    snprintf(
        deviceId,
        sizeof(deviceId),
        "robotbooth-%012llx",
        static_cast<unsigned long long>(chipId));
    snprintf(colorTopic, sizeof(colorTopic), "robobooth/v1/devices/%s/state/color", deviceId);
    snprintf(sensorTopic, sizeof(sensorTopic), "robobooth/v1/devices/%s/telemetry/sensors", deviceId);
    snprintf(diagnosticLogTopic, sizeof(diagnosticLogTopic), "robobooth/v1/devices/%s/telemetry/logs", deviceId);
    snprintf(statusTopic, sizeof(statusTopic), "robobooth/v1/devices/%s/status", deviceId);
    snprintf(programDeployTopic, sizeof(programDeployTopic), "robobooth/v1/devices/%s/program/deploy", deviceId);
    snprintf(programControlTopic, sizeof(programControlTopic), "robobooth/v1/devices/%s/program/control", deviceId);
    snprintf(programStatusTopic, sizeof(programStatusTopic), "robobooth/v1/devices/%s/program/status", deviceId);
    snprintf(hardwareConfigurationTopic, sizeof(hardwareConfigurationTopic), "robobooth/v1/devices/%s/hardware/config", deviceId);
    snprintf(hardwareConfigurationStatusTopic, sizeof(hardwareConfigurationStatusTopic), "robobooth/v1/devices/%s/hardware/status", deviceId);
    programRuntime = new ProgramRuntime(
        hardwareConfiguration,
        [](const char* requestId, const char* state, const char* error) { queueProgramStatus(requestId, state, error); },
        [](uint8_t red, uint8_t green, uint8_t blue) {
            currentColor = {red, green, blue};
            showStatus(red, green, blue);
        },
        [](const char* level, const String& message) {
            logDiagnostic(level, "runtime", "%s", message.c_str());
        });
    mqttClient.onMessage(receiveMqttMessage);

    provisioningQueue = xQueueCreate(1, sizeof(ProvisioningMessage));
    if (provisioningQueue == nullptr) {
        showStatus(24, 0, 0);
        logDiagnostic("error", "startup", "Failed to allocate the provisioning queue. Restarting in 5 seconds.");
        delay(5000);
        ESP.restart();
    }

    if (!NimBLEDevice::init(DeviceName)) {
        showStatus(24, 0, 0);
        logDiagnostic("error", "bluetooth", "Failed to initialize Bluetooth. Restarting in 5 seconds.");
        delay(5000);
        ESP.restart();
    }

    NimBLEDevice::setSecurityIOCap(BLE_HS_IO_DISPLAY_ONLY);
    NimBLEDevice::setSecurityAuth(true, true, true);
    NimBLEDevice::setSecurityPasskey(PairingPasskey);

    NimBLEServer* server = NimBLEDevice::createServer();
    server->setCallbacks(&serverCallbacks);
    server->advertiseOnDisconnect(true);

    NimBLEService* service = server->createService(ServiceUuid);
    NimBLECharacteristic* statusCharacteristic = service->createCharacteristic(
        StatusCharacteristicUuid,
        NIMBLE_PROPERTY::READ | NIMBLE_PROPERTY::READ_AUTHEN);
    statusCharacteristic->setValue("ready");

    NimBLECharacteristic* provisioningCharacteristic = service->createCharacteristic(
        ProvisioningCharacteristicUuid,
        NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_AUTHEN,
        20);
    provisioningCharacteristic->setCallbacks(&provisioningCallbacks);

    provisioningStatusCharacteristic = service->createCharacteristic(
        ProvisioningStatusCharacteristicUuid,
        NIMBLE_PROPERTY::READ | NIMBLE_PROPERTY::READ_AUTHEN,
        16);
    provisioningStatusCharacteristic->setValue("idle");

    if (!server->start()) {
        showStatus(24, 0, 0);
        logDiagnostic("error", "bluetooth", "Failed to start the BLE GATT server. Restarting in 5 seconds.");
        delay(5000);
        ESP.restart();
    }

    NimBLEAdvertising* advertising = NimBLEDevice::getAdvertising();
    advertising->setName(DeviceName);
    advertising->addServiceUUID(ServiceUuid);
    advertising->enableScanResponse(true);

    if (!advertising->start()) {
        showStatus(24, 0, 0);
        logDiagnostic("error", "bluetooth", "Failed to start BLE advertising. Restarting in 5 seconds.");
        delay(5000);
        ESP.restart();
    }

    // Bring up Bluetooth before the Wi-Fi radio. On the ESP32-S3 this avoids a
    // coexistence-controller abort that can occur when an already-started Wi-Fi
    // station is followed immediately by BLE controller initialization.
    // Provisioning values are kept only in RAM. The server computer owns their
    // DPAPI-protected persistence and sends them again after every board restart.
    WiFi.persistent(false);
    WiFi.mode(WIFI_STA);
    // ESP32-S3 radio coexistence requires modem sleep while BLE is active.
    WiFi.setSleep(true);
    WiFi.setAutoReconnect(true);
    WiFi.disconnect(false, true);

    logDiagnostic("info", "bluetooth", "Advertising as %s (%s)", DeviceName, deviceId);
    Serial.printf("Pair using passkey %06lu\n", static_cast<unsigned long>(PairingPasskey));
}

void loop() {
    if (programRuntime == nullptr || !programRuntime->running()) updateIdleStatusLight();
    if (programRuntime != nullptr) programRuntime->tick();
    startPendingProvisioning();
    updateNetworkConnections();
    delay(10);
}
