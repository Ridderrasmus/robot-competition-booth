# Robot Competition Booth Firmware

PlatformIO firmware for an ESP32-S3 board using the Arduino framework and NimBLE-Arduino.

## Requirements

- PlatformIO Core (`pio`) on `PATH`
- A data-capable USB cable
- The ESP32-S3-WROOM-1-N16R8 development board
- Windows access to the board's serial port

## Current behavior

- Advertises a connectable Bluetooth Low Energy peripheral using the name compiled into the image; the default is `RobotBooth-ESP32S3`.
- Starts authenticated, bonded BLE pairing as soon as a client connects.
- Uses the pairing passkey compiled into the image; the default is `000123`.
- Exposes a small authenticated status characteristic whose value is `ready`.
- Accepts a secured or open Wi-Fi network and the app's authenticated MQTT endpoint together through a write-only provisioning characteristic using 20-byte-or-smaller packets.
- Exposes an authenticated provisioning status characteristic so the server can wait for `mqtt-connected` or report a Wi-Fi/MQTT failure.
- Keeps provisioned Wi-Fi and MQTT credentials in RAM rather than persisting them to the ESP32 flash, and discards them after a failed Wi-Fi connection.
- Slowly cycles the built-in RGB LED through red, green, and blue over an 18-second loop.
- Publishes its device name and current `#RRGGBB` colour to the app's embedded MQTT broker once per second.
- Publishes a contract-valid synthetic sensor snapshot five times per second so the programming interface can be
  tested before physical sensor drivers are connected. Distance, detected colour, light, all five line channels,
  line position, both encoders/speeds, and all five servo angles continuously change over time.
- Publishes retained online/offline state and automatically reconnects to Wi-Fi and MQTT.
- Subscribes to its authenticated, device-specific `hardware/config` topic. Valid server mappings are applied without
  rebooting; invalid, duplicate, incomplete, reserved, or capability-incompatible GPIO assignments are rejected.
- Configures optional left/right motor PWM and direction, encoder pairs, five servos, ultrasonic trigger/echo,
  colour-sensor I²C, and a five-channel analogue line array. Components omitted by the server remain inert.
- Acknowledges each hardware mapping on `hardware/status`, allowing the admin page to distinguish saved configuration
  from configuration actually accepted by the running robot.
- Queues up to 32 diagnostic messages and publishes them on the authenticated robot-specific `telemetry/logs`
  topic for its admin-only terminal. Pairing passkeys remain local to USB serial output.
- Restarts advertising after a client disconnects.
- Uses red as a fatal-startup indicator; during normal operation the built-in RGB LED runs the colour animation.

The admin firmware-flashing page supplies a `RobotBooth-` name suffix and a 1–6 digit connection code to each
build. BLE passkeys are always represented as six digits, so a code such as `123` appears as `000123` in pairing
dialogs. Ordinary PlatformIO builds use the defaults above unless `ROBOBOOTH_DEVICE_NAME` and
`ROBOBOOTH_PAIRING_PASSKEY` are supplied as build defines.

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

The device is a custom BLE peripheral rather than a Bluetooth Classic device. The web application securely provisions it after pairing; configure the website's Wi-Fi setup page first, then connect from its Bluetooth page. For manual inspection, connect with a BLE-capable client and access an authenticated characteristic to trigger pairing. Enter `000123` when the client prompts for the passkey.

The RGB LED provides a basic check without a serial monitor:

- Smooth colour loop: firmware is running; this is the same colour reported to the web app.
- Red held for five seconds: BLE startup failed and the board will restart.

Wi-Fi and MQTT settings intentionally are not retained across board restarts. The Windows host stores them securely and sends them again whenever it establishes a Robobooth BLE connection.

Hardware mappings are likewise owned by the Windows host, but are delivered over authenticated MQTT after the robot
connects. The broker retains the current per-robot mapping and reloads it from server storage after an application restart.
GPIO48 remains reserved for the built-in status light; flash/PSRAM, USB, and unsafe output pins are rejected.
On the N16R8 module, GPIO35–37 are also withheld because its octal PSRAM uses them internally.
Analogue line inputs are restricted to ADC1 GPIO1–10 so they remain usable while Wi-Fi is active.
Strapping GPIO0, GPIO3, GPIO45, and GPIO46 are not offered for generic robot connections.

Synthetic snapshots are published on `robobooth/v1/devices/<device-id>/telemetry/sensors` with `mode: "idle"` and
match `docs/contracts/schemas/sensor-snapshot.schema.json`. Replace `publishSyntheticSensorSnapshot` with hardware
cache readings when the physical drivers are ready; the topic and payload contract should remain unchanged.

Generic BLE devices may not appear in a phone's normal Bluetooth settings. Use a BLE scanner such as nRF Connect when diagnosing advertisements or custom GATT services.

If a phone or computer has already bonded with an older firmware passkey, remove/forget the device on that phone or computer before testing pairing again. The ESP32 may also retain bond information in flash.

## Important source files

- `platformio.ini` pins the ESP32 platform, Arduino framework, NimBLE-Arduino and MQTT dependencies, and serial ports.
- `boards/esp32-s3-n16r8.json` defines the local 16 MB flash and 8 MB OPI PSRAM board profile.
- `src/main.cpp` configures the BLE server, security, Wi-Fi/MQTT provisioning protocol, colour telemetry, advertising, and RGB animation.
