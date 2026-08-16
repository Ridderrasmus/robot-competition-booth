# Robot Competition Booth

This repository contains the server-side control website and ESP32-S3 firmware for the Robot Competition Booth project.

## Repository layout

All buildable source is kept under `src` so documentation and other supporting material can remain separate:

```text
src/
|-- RobotCompetitionBooth.slnx
|-- RobotCompetitionBooth.Web/       # Interactive Blazor Server application
`-- RobotCompetitionBooth.Firmware/  # PlatformIO ESP32-S3 firmware
```

Each project has its own setup and operating notes:

- [Blazor Server README](src/RobotCompetitionBooth.Web/README.md)
- [ESP32-S3 firmware README](src/RobotCompetitionBooth.Firmware/README.md)

## Quick start

Build and run the website on Windows:

```powershell
dotnet build src/RobotCompetitionBooth.slnx
dotnet run --project src/RobotCompetitionBooth.Web
```

Publish the complete Windows host, including its MQTT broker and .NET runtime, as one executable:

```powershell
dotnet publish src/RobotCompetitionBooth.Web -p:PublishProfile=WindowsSingleFile
```

Run `src/RobotCompetitionBooth.Web/bin/publish/win-x64/RobotCompetitionBooth.Web.exe`, then open
`http://localhost:5000` unless an alternative ASP.NET Core URL has been configured.

Build and upload the firmware:

```powershell
Set-Location src/RobotCompetitionBooth.Firmware
pio run
pio run --target upload
```

The firmware advertises as `RobotBooth-ESP32S3`. Unlock **Admin**, open **Wi-Fi setup**, scan with the Windows host's Wi-Fi adapter, and select the network the device should use. The host's current Wi-Fi connection is marked and pinned first; secured networks request a password, while open networks can be saved without one. Then use **Bluetooth setup** inside Admin to scan, pair, provision Wi-Fi and MQTT, and maintain the robot connection. The device slowly cycles its RGB LED and sends telemetry over Wi-Fi; **Live devices** now shows only the connected-robot list and programming buttons. Bluetooth and Wi-Fi discovery use the adapters installed in the server computer, not the browser visitor's adapters.

Selecting **Disconnect and forget** removes every saved `RobotBooth-*` pairing from Windows. The app performs the same sweep after a failed post-pairing connection, when the executable shuts down normally, and at the next startup as recovery from a forced exit.

## Direction

The initial connection and Wi-Fi/MQTT provisioning path uses authenticated Bluetooth Low Energy. Runtime colour
telemetry uses the MQTT broker hosted inside the same Blazor executable. Blockly-based visual programming is
available from each connected device, synchronizes live edits and collaborator presence per robot, and saves named
per-device workspaces on the server. The editor includes the complete robot block catalog and a live sensor inspector.
The versioned [robot/web MQTT and instruction contract](docs/contracts/README.md) plus the initial Python workspace
compiler are checked in; device-side instruction storage/execution and the final upload control path remain the next
firmware integration step.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
