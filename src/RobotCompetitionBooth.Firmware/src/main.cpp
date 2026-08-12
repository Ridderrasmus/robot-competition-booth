#include <Arduino.h>
#include <esp32-hal-rgb-led.h>
#include <NimBLEDevice.h>

namespace {

constexpr char DeviceName[] = "RobotBooth-ESP32S3";

// BLE passkeys are six digits. The requested three-digit code is represented
// with leading zeroes in pairing dialogs: enter 000123.
constexpr uint32_t PairingPasskey = 123;

constexpr char ServiceUuid[] = "8ddf7a40-7520-4e57-9e32-9b6b091c5c8b";
constexpr char StatusCharacteristicUuid[] = "f6eb0c76-11a6-4a5f-a58d-6b55d94ff31a";

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

} // namespace

void setup() {
    showStatus(24, 8, 0);

    Serial.begin(115200);
    delay(500);

    Serial.println();
    Serial.println("Starting Robot Competition Booth BLE peripheral...");
    Serial.printf("Detected PSRAM: %u bytes\n", ESP.getPsramSize());

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

    delay(100);
}
