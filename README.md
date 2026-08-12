# Robot Competition Booth

A unified project for controlling a premade Arduino-based robot through a compact Blazor Server application with a Blockly programming interface.

## Source layout

All application and firmware source code lives under `src`:

```text
src/
├── RobotCompetitionBooth.slnx
├── RobotCompetitionBooth.Web/          # Interactive Blazor Server app
└── RobotCompetitionBooth.Firmware/     # PlatformIO project workspace
    ├── include/
    ├── lib/
    ├── src/
    └── test/
```

Documentation and other supporting files can remain outside `src` without being mixed into the buildable projects.

## Run the web app

The web app targets .NET 10. From the repository root, run:

```powershell
dotnet run --project src/RobotCompetitionBooth.Web
```

To build the entire solution:

```powershell
dotnet build src/RobotCompetitionBooth.slnx
```

## Initialize the firmware project

The firmware workspace has the standard PlatformIO directory layout, but no board is selected yet. Once the robot controller board is known, initialize it from the repository root:

```powershell
Set-Location src/RobotCompetitionBooth.Firmware
pio project init --board <board-id>
```

## Planned system design

### Visual programming with Blockly

- The Blazor Server app embeds Blockly for drag-and-drop robot programming.
- Blockly blocks represent robot capabilities such as movement, sensors, conditions, loops, and actions.
- Generated commands are exported to JSON.

### Python compiler

- A Python application will convert a Blockly workspace JSON document into robot-compatible opcodes.
- It will validate the generated program before deployment and may eventually simulate robot execution.

### Initial robot configuration via Bluetooth

- During onboarding, the app connects to the robot over Bluetooth.
- Bluetooth handles device pairing, first-time configuration, and Wi-Fi provisioning.

### Runtime communication via Wi-Fi and MQTT

- After setup, the robot joins the configured Wi-Fi network.
- App-to-robot communication transitions to MQTT over Wi-Fi.
- The MQTT broker runs inside the app to keep deployment simple and lightweight.

## Parts list

To be filled with data from the project parts spreadsheet.

- _No parts added yet._

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
