#include <Arduino.h>
#include <esp32-hal-rgb-led.h>
#include <MQTT.h>
#include <NimBLEDevice.h>
#include <WiFi.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include "ProgramRuntime.h"

#include <algorithm>
#include <cstring>
#include <vector>

namespace {

constexpr char DeviceName[] = "RobotBooth-ESP32S3";
constexpr char MqttUsername[] = "robobooth";

// BLE passkeys are six digits. The requested three-digit code is represented
// with leading zeroes in pairing dialogs: enter 000123.
constexpr uint32_t PairingPasskey = 123;

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
constexpr unsigned long LedAnimationPeriodMs = 18000;
constexpr unsigned long LedRefreshIntervalMs = 30;
constexpr uint8_t LedBrightness = 28;

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

NimBLECharacteristic* provisioningStatusCharacteristic = nullptr;
QueueHandle_t provisioningQueue = nullptr;
WiFiClient wifiClient;
MQTTClient mqttClient(256 * 1024);

char deviceId[32]{};
char colorTopic[128]{};
char sensorTopic[128]{};
char statusTopic[128]{};
char programDeployTopic[128]{};
char programControlTopic[128]{};
char programStatusTopic[128]{};
char mqttHost[MaximumMqttHostLength + 1]{};
char mqttPassword[MqttPasswordLength + 1]{};
uint16_t mqttPort = 0;

bool mqttConfigured = false;
bool wifiWasConnected = false;
bool initialWifiConnection = false;
bool mqttFailureReported = false;
unsigned long wifiConnectionStartedAt = 0;
unsigned long mqttConnectionStartedAt = 0;
unsigned long lastMqttConnectAttemptAt = 0;
unsigned long lastColorPublishedAt = 0;
unsigned long lastSensorPublishedAt = 0;
unsigned long lastSensorPublishErrorAt = 0;
unsigned long lastLedRefreshAt = 0;
uint32_t colorSequence = 0;
uint32_t sensorSequence = 0;
RgbColor currentColor{255, 0, 0};
HardwareConfiguration hardwareConfiguration;
ProgramRuntime* programRuntime = nullptr;
uint32_t programStatusSequence = 0;

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

void receiveMqttMessage(String& topic, String& payload) {
    if (programRuntime == nullptr) return;
    String error;
    bool accepted = false;
    if (topic == programDeployTopic) accepted = programRuntime->deploy(payload, error);
    else if (topic == programControlTopic) accepted = programRuntime->control(payload, error);
    if (!accepted && !error.isEmpty()) publishProgramStatus("", "failed", error.c_str());
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

RgbColor colorForTime(unsigned long now) {
    const uint32_t wheelPosition =
        (static_cast<uint64_t>(now % LedAnimationPeriodMs) * 768) / LedAnimationPeriodMs;

    if (wheelPosition < 256) {
        return {
            static_cast<uint8_t>(255 - wheelPosition),
            static_cast<uint8_t>(wheelPosition),
            0};
    }

    if (wheelPosition < 512) {
        const uint16_t offset = wheelPosition - 256;
        return {
            0,
            static_cast<uint8_t>(255 - offset),
            static_cast<uint8_t>(offset)};
    }

    const uint16_t offset = wheelPosition - 512;
    return {
        static_cast<uint8_t>(offset),
        0,
        static_cast<uint8_t>(255 - offset)};
}

void updateLedAnimation() {
    const unsigned long now = millis();
    if (now - lastLedRefreshAt < LedRefreshIntervalMs) {
        return;
    }

    lastLedRefreshAt = now;
    currentColor = colorForTime(now);
    showStatus(
        static_cast<uint8_t>((static_cast<uint16_t>(currentColor.red) * LedBrightness) / 255),
        static_cast<uint8_t>((static_cast<uint16_t>(currentColor.green) * LedBrightness) / 255),
        static_cast<uint8_t>((static_cast<uint16_t>(currentColor.blue) * LedBrightness) / 255));
}

class ServerCallbacks final : public NimBLEServerCallbacks {
    void onConnect(NimBLEServer* server, NimBLEConnInfo& connection) override {
        Serial.printf(
            "BLE client connected: %s\n",
            connection.getAddress().toString().c_str());
        Serial.printf("Pairing passkey: %06lu\n", static_cast<unsigned long>(PairingPasskey));

        int returnCode = 0;
        if (!NimBLEDevice::startSecurity(connection.getConnHandle(), &returnCode)) {
            Serial.printf("Could not start BLE security (code %d); disconnecting.\n", returnCode);
            server->disconnect(connection.getConnHandle());
        }
    }

    void onDisconnect(
        NimBLEServer*,
        NimBLEConnInfo& connection,
        int reason) override {
        Serial.printf(
            "BLE client disconnected: %s (reason %d)\n",
            connection.getAddress().toString().c_str(),
            reason);
    }

    uint32_t onPassKeyDisplay() override {
        Serial.printf("Enter passkey %06lu on the pairing device.\n", static_cast<unsigned long>(PairingPasskey));
        return PairingPasskey;
    }

    void onAuthenticationComplete(NimBLEConnInfo& connection) override {
        if (!connection.isEncrypted() || !connection.isAuthenticated()) {
            Serial.println("BLE authentication failed; disconnecting client.");
            NimBLEDevice::getServer()->disconnect(connection.getConnHandle());
            return;
        }

        Serial.printf(
            "BLE pairing succeeded: %s\n",
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
        Serial.println("Could not publish the current colour over MQTT.");
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
            Serial.println("Synthetic sensor payload exceeded its buffer.");
        }
        return;
    }

    if (!mqttClient.publish(sensorTopic, payload, false, 0) &&
        now - lastSensorPublishErrorAt >= SensorPublishErrorLogIntervalMs) {
        lastSensorPublishErrorAt = now;
        Serial.println("Could not publish synthetic sensor telemetry over MQTT.");
    }
}

void configureMqttClient() {
    mqttClient.begin(mqttHost, mqttPort, wifiClient);
    mqttClient.setOptions(15, true, 1000);
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
        mqttFailureReported = false;
        setProvisioningStatus("mqtt-connected");
        mqttClient.publish(statusTopic, "online", true, 1);
        mqttClient.subscribe(programDeployTopic, 1);
        mqttClient.subscribe(programControlTopic, 1);
        publishColor(true);
        publishSyntheticSensorSnapshot(true);
        Serial.println("Connected to the embedded MQTT broker.");
        return;
    }

    if (!mqttFailureReported && now - mqttConnectionStartedAt >= InitialMqttConnectionTimeoutMs) {
        mqttFailureReported = true;
        setProvisioningStatus("mqtt-failed");
        Serial.println("Could not connect to the embedded MQTT broker; retrying in the background.");
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

    if (mqttClient.connected()) {
        mqttClient.publish(statusTopic, "offline", true, 1);
        mqttClient.disconnect();
    }

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
    Serial.println("Received Wi-Fi and MQTT settings over authenticated BLE; connecting.");
}

void updateNetworkConnections() {
    const bool wifiConnected = WiFi.status() == WL_CONNECTED;
    if (!wifiConnected) {
        if (wifiWasConnected) {
            wifiWasConnected = false;
            setProvisioningStatus("wifi-connecting");
            Serial.println("Wi-Fi connection was lost; waiting for automatic reconnection.");
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
            Serial.println("Wi-Fi connection failed; provisioned settings were discarded.");
        }

        return;
    }

    if (!wifiWasConnected) {
        wifiWasConnected = true;
        initialWifiConnection = false;
        configureMqttClient();
        Serial.println("Wi-Fi connection established; connecting to MQTT.");
    }

    mqttClient.loop();
    tryConnectMqtt();
    publishColor();
    publishSyntheticSensorSnapshot();
}

} // namespace

void setup() {
    showStatus(24, 8, 0);

    Serial.begin(115200);
    delay(500);

    Serial.println();
    Serial.println("Starting Robot Competition Booth firmware...");
    Serial.printf("Detected PSRAM: %u bytes\n", ESP.getPsramSize());

    const uint64_t chipId = ESP.getEfuseMac() & 0xFFFFFFFFFFFFULL;
    snprintf(
        deviceId,
        sizeof(deviceId),
        "robotbooth-%012llx",
        static_cast<unsigned long long>(chipId));
    snprintf(colorTopic, sizeof(colorTopic), "robobooth/v1/devices/%s/state/color", deviceId);
    snprintf(sensorTopic, sizeof(sensorTopic), "robobooth/v1/devices/%s/telemetry/sensors", deviceId);
    snprintf(statusTopic, sizeof(statusTopic), "robobooth/v1/devices/%s/status", deviceId);
    snprintf(programDeployTopic, sizeof(programDeployTopic), "robobooth/v1/devices/%s/program/deploy", deviceId);
    snprintf(programControlTopic, sizeof(programControlTopic), "robobooth/v1/devices/%s/program/control", deviceId);
    snprintf(programStatusTopic, sizeof(programStatusTopic), "robobooth/v1/devices/%s/program/status", deviceId);
    programRuntime = new ProgramRuntime(
        hardwareConfiguration,
        [](const char* requestId, const char* state, const char* error) { publishProgramStatus(requestId, state, error); },
        [](uint8_t red, uint8_t green, uint8_t blue) {
            currentColor = {red, green, blue};
            showStatus(red, green, blue);
        });
    mqttClient.onMessage(receiveMqttMessage);

    // Provisioning values are kept only in RAM. The server computer owns their
    // DPAPI-protected persistence and sends them again after every board restart.
    WiFi.persistent(false);
    WiFi.mode(WIFI_STA);
    WiFi.setAutoReconnect(true);
    WiFi.disconnect(false, true);

    provisioningQueue = xQueueCreate(1, sizeof(ProvisioningMessage));
    if (provisioningQueue == nullptr) {
        showStatus(24, 0, 0);
        Serial.println("Failed to allocate the provisioning queue. Restarting in 5 seconds.");
        delay(5000);
        ESP.restart();
    }

    if (!NimBLEDevice::init(DeviceName)) {
        showStatus(24, 0, 0);
        Serial.println("Failed to initialize Bluetooth. Restarting in 5 seconds.");
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
        Serial.println("Failed to start the BLE GATT server. Restarting in 5 seconds.");
        delay(5000);
        ESP.restart();
    }

    NimBLEAdvertising* advertising = NimBLEDevice::getAdvertising();
    advertising->setName(DeviceName);
    advertising->addServiceUUID(ServiceUuid);
    advertising->enableScanResponse(true);

    if (!advertising->start()) {
        showStatus(24, 0, 0);
        Serial.println("Failed to start BLE advertising. Restarting in 5 seconds.");
        delay(5000);
        ESP.restart();
    }

    Serial.printf("Advertising as %s (%s)\n", DeviceName, deviceId);
    Serial.printf("Pair using passkey %06lu\n", static_cast<unsigned long>(PairingPasskey));
}

void loop() {
    if (programRuntime == nullptr || !programRuntime->running()) updateLedAnimation();
    if (programRuntime != nullptr) programRuntime->tick();
    startPendingProvisioning();
    updateNetworkConnections();
    delay(10);
}
