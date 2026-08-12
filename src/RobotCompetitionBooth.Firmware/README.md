# Robot Competition Booth Firmware

PlatformIO firmware for an ESP32-S3 board using the Arduino framework and NimBLE-Arduino.

## Requirements

- PlatformIO Core (`pio`) on `PATH`
- A data-capable USB cable
- The ESP32-S3-WROOM-1-N16R8 development board
- Windows access to the board's serial port

## Current behavior

- Advertises a connectable Bluetooth Low Energy peripheral named `RobotBooth-ESP32S3`.
- Starts authenticated, bonded BLE pairing as soon as a client connects.
- Uses the constant passkey `000123`.
- Exposes a small authenticated status characteristic whose value is `ready`.
- Restarts advertising after a client disconnects.
- Uses the built-in RGB LED as a boot status indicator: amber while starting, green while BLE is advertising, and red if BLE startup fails.

BLE passkeys are always represented as six digits. The requested three-digit code `123` therefore appears as `000123` in pairing dialogs.

The project-local `esp32-s3-n16r8` board profile matches the ESP32-S3-WROOM-1-N16R8 module used here:

- 16 MB QIO flash
- 8 MB OPI PSRAM
- 16 MB partition table
- Native USB CDC enabled at boot
- Generic ESP32-S3 pin mapping

The board's built-in WS2812 RGB LED is connected to GPIO48.

## Build and upload

From the repository root:

```powershell
Set-Location src/RobotCompetitionBooth.Firmware
pio run
pio run --target upload
pio device monitor
```

Upload and serial monitoring are configured for `COM4` at 115200 baud. The board originally appeared as COM3, then Windows assigned COM4 when it entered USB download mode; COM4 remained stable after flashing and rebooting.

If Windows assigns a different port, update `upload_port` and `monitor_port` in `platformio.ini`, or override the upload port once:

```powershell
pio run --target upload --upload-port COM5
```

If automatic upload cannot connect, hold **BOOT**, tap **RESET**, release **BOOT**, and retry the upload. Tap **RESET** normally after flashing if the board remains in download mode.

## Pairing and verification

The device is a custom BLE peripheral rather than a Bluetooth Classic device. Connect with a BLE-capable client, then access the authenticated status characteristic to trigger pairing. Enter `000123` when the client prompts for the passkey.

The RGB LED provides a boot-state check without a serial monitor:

- Amber: firmware is starting.
- Green: the GATT server is running and BLE advertising started.
- Red: BLE startup failed and the board will restart.

Generic BLE devices may not appear in a phone's normal Bluetooth settings. Use a BLE scanner such as nRF Connect when diagnosing advertisements or custom GATT services.

If a phone or computer has already bonded with an older firmware passkey, remove/forget the device on that phone or computer before testing pairing again. The ESP32 may also retain bond information in flash.

## Important source files

- `platformio.ini` pins the ESP32 platform, Arduino framework, NimBLE-Arduino dependency, and serial ports.
- `boards/esp32-s3-n16r8.json` defines the local 16 MB flash and 8 MB OPI PSRAM board profile.
- `src/main.cpp` configures the BLE server, security, GATT service, advertising, and RGB status LED.
