#include <Arduino.h>
#include <esp32-hal-rgb-led.h>
#include <WiFi.h>
#include <NimBLEDevice.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>

#include <algorithm>
#include <cstring>
#include <vector>

namespace {

constexpr char DeviceName[] = "RobotBooth-ESP32S3";

// BLE passkeys are six digits. The requested three-digit code is represented
// with leading zeroes in pairing dialogs: enter 000123.
constexpr uint32_t PairingPasskey = 123;

constexpr char ServiceUuid[] = "8ddf7a40-7520-4e57-9e32-9b6b091c5c8b";
constexpr char StatusCharacteristicUuid[] = "f6eb0c76-11a6-4a5f-a58d-6b55d94ff31a";
constexpr char WifiProvisioningCharacteristicUuid[] = "0a7c0061-88a4-43cc-a010-faf7595da303";
constexpr char WifiStatusCharacteristicUuid[] = "0a7c0062-88a4-43cc-a010-faf7595da303";

constexpr uint8_t ProvisioningProtocolVersion = 1;
constexpr uint8_t ProvisioningStartCommand = 1;
constexpr uint8_t ProvisioningDataCommand = 2;
constexpr uint8_t ProvisioningCommitCommand = 3;
constexpr size_t MaximumSsidLength = 32;
constexpr size_t MinimumPasswordLength = 8;
constexpr size_t MaximumPasswordLength = 64;
constexpr unsigned long WifiConnectionTimeoutMs = 20000;

struct WifiCredentialsMessage {
    char ssid[MaximumSsidLength + 1];
    char password[MaximumPasswordLength + 1];
};

NimBLECharacteristic* wifiStatusCharacteristic = nullptr;
QueueHandle_t wifiCredentialQueue = nullptr;
bool wifiConnecting = false;
unsigned long wifiConnectionStartedAt = 0;

void setWifiStatus(const char* status) {
    if (wifiStatusCharacteristic != nullptr) {
        wifiStatusCharacteristic->setValue(status);
    }
}

void showStatus(uint8_t red, uint8_t green, uint8_t blue) {
    neopixelWrite(RGB_BUILTIN, red, green, blue);
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

class WifiProvisioningCallbacks final : public NimBLECharacteristicCallbacks {
    size_t expectedSsidLength = 0;
    size_t expectedPasswordLength = 0;
    std::vector<uint8_t> payload;

    void resetPayload() {
        std::fill(payload.begin(), payload.end(), 0);
        payload.clear();
        expectedSsidLength = 0;
        expectedPasswordLength = 0;
    }

    void rejectPayload() {
        resetPayload();
        setWifiStatus("invalid");
    }

    void startPayload(const uint8_t* data, size_t length) {
        if (length != 4) {
            rejectPayload();
            return;
        }

        expectedSsidLength = data[2];
        expectedPasswordLength = data[3];
        if (expectedSsidLength == 0 ||
            expectedSsidLength > MaximumSsidLength ||
            expectedPasswordLength < MinimumPasswordLength ||
            expectedPasswordLength > MaximumPasswordLength) {
            rejectPayload();
            return;
        }

        payload.clear();
        payload.reserve(expectedSsidLength + expectedPasswordLength);
        setWifiStatus("receiving");
    }

    void appendPayload(const uint8_t* data, size_t length) {
        if (length <= 4 || expectedSsidLength == 0) {
            rejectPayload();
            return;
        }

        const size_t offset = static_cast<size_t>(data[2]) |
            (static_cast<size_t>(data[3]) << 8);
        const size_t chunkLength = length - 4;
        const size_t expectedLength = expectedSsidLength + expectedPasswordLength;
        if (offset != payload.size() || payload.size() + chunkLength > expectedLength) {
            rejectPayload();
            return;
        }

        payload.insert(payload.end(), data + 4, data + length);
    }

    void commitPayload(size_t length) {
        const size_t expectedLength = expectedSsidLength + expectedPasswordLength;
        if (length != 2 || expectedSsidLength == 0 || payload.size() != expectedLength ||
            std::find(payload.begin(), payload.end(), 0) != payload.end() ||
            wifiCredentialQueue == nullptr) {
            rejectPayload();
            return;
        }

        const auto passwordStart = payload.begin() + expectedSsidLength;
        const bool invalidRawPsk = expectedPasswordLength == MaximumPasswordLength &&
            std::any_of(passwordStart, payload.end(), [](uint8_t character) {
                return !((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F'));
            });
        if (invalidRawPsk) {
            rejectPayload();
            return;
        }

        WifiCredentialsMessage credentials{};
        std::memcpy(credentials.ssid, payload.data(), expectedSsidLength);
        std::memcpy(
            credentials.password,
            payload.data() + expectedSsidLength,
            expectedPasswordLength);

        if (xQueueOverwrite(wifiCredentialQueue, &credentials) != pdPASS) {
            std::memset(&credentials, 0, sizeof(credentials));
            rejectPayload();
            return;
        }

        std::memset(&credentials, 0, sizeof(credentials));
        resetPayload();
        setWifiStatus("queued");
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

WifiProvisioningCallbacks wifiProvisioningCallbacks;

void startPendingWifiConnection() {
    if (wifiCredentialQueue == nullptr) {
        return;
    }

    WifiCredentialsMessage credentials{};
    if (xQueueReceive(wifiCredentialQueue, &credentials, 0) != pdPASS) {
        return;
    }

    WiFi.disconnect(false, true);
    WiFi.begin(credentials.ssid, credentials.password);
    std::memset(&credentials, 0, sizeof(credentials));

    wifiConnecting = true;
    wifiConnectionStartedAt = millis();
    setWifiStatus("connecting");
    showStatus(16, 16, 0);
    Serial.println("Received Wi-Fi credentials over authenticated BLE; connecting.");
}

void updateWifiConnectionStatus() {
    if (!wifiConnecting) {
        return;
    }

    if (WiFi.status() == WL_CONNECTED) {
        wifiConnecting = false;
        setWifiStatus("connected");
        showStatus(0, 0, 24);
        Serial.println("Wi-Fi connection established.");
        return;
    }

    if (millis() - wifiConnectionStartedAt >= WifiConnectionTimeoutMs) {
        wifiConnecting = false;
        WiFi.disconnect(false, true);
        setWifiStatus("failed");
        showStatus(0, 24, 0);
        Serial.println("Wi-Fi connection failed; credentials were discarded.");
    }
}

} // namespace

void setup() {
    showStatus(24, 8, 0);

    Serial.begin(115200);
    delay(500);

    Serial.println();
    Serial.println("Starting Robot Competition Booth BLE peripheral...");
    Serial.printf("Detected PSRAM: %u bytes\n", ESP.getPsramSize());

    // Wi-Fi credentials are provisioned for each BLE connection and kept only
    // in RAM on this board. The server computer owns their secure persistence.
    WiFi.persistent(false);
    WiFi.mode(WIFI_STA);
    WiFi.disconnect(false, true);

    wifiCredentialQueue = xQueueCreate(1, sizeof(WifiCredentialsMessage));
    if (wifiCredentialQueue == nullptr) {
        showStatus(24, 0, 0);
        Serial.println("Failed to allocate the Wi-Fi credential queue. Restarting in 5 seconds.");
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

    NimBLECharacteristic* wifiProvisioningCharacteristic = service->createCharacteristic(
        WifiProvisioningCharacteristicUuid,
        NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_AUTHEN,
        20);
    wifiProvisioningCharacteristic->setCallbacks(&wifiProvisioningCallbacks);

    wifiStatusCharacteristic = service->createCharacteristic(
        WifiStatusCharacteristicUuid,
        NIMBLE_PROPERTY::READ | NIMBLE_PROPERTY::READ_AUTHEN,
        16);
    wifiStatusCharacteristic->setValue("idle");

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

    Serial.printf("Advertising as %s\n", DeviceName);
    Serial.printf("Pair using passkey %06lu\n", static_cast<unsigned long>(PairingPasskey));
    showStatus(0, 24, 0);
}

void loop() {
    static bool startupConfirmed = false;
    if (!startupConfirmed && millis() >= 3000) {
        Serial.println("BLE firmware is running and advertising is active.");
        startupConfirmed = true;
    }

    startPendingWifiConnection();
    updateWifiConnectionStatus();

    delay(100);
}
