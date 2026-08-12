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

Build and upload the firmware:

```powershell
Set-Location src/RobotCompetitionBooth.Firmware
pio run
pio run --target upload
```

The firmware advertises as `RobotBooth-ESP32S3`. The website's Bluetooth page scans using the Bluetooth adapter installed in the computer running the server, not the browser visitor's Bluetooth adapter.

## Direction

The initial connection and provisioning path uses Bluetooth Low Energy. The intended runtime path will move robot communication to Wi-Fi and MQTT after configuration. Blockly-based visual programming and a Python opcode compiler are planned but are not implemented yet.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
