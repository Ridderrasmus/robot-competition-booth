# Robot Competition Booth Web

Interactive Blazor Server application for discovering and managing the robot from the Windows computer hosting the website.

## Current behavior

- Runs interactive Razor components on the server.
- Provides a password-gated `/admin` area for Wi-Fi and Bluetooth configuration.
- Provides an `/admin/bluetooth` page that scans for Bluetooth Classic and Bluetooth Low Energy devices after admin unlock.
- Uses the Windows Bluetooth adapter attached to the server computer.
- Provides an `/admin/wifi` setup page that scans with the host's Windows Wi-Fi adapter, pins and marks the host's current connection, and supports secured and open networks.
- Stores the selected network credentials in Local AppData encrypted with Windows DPAPI for the current user.
- Blocks Robobooth BLE connections until Wi-Fi credentials have been configured.
- Hosts an authenticated MQTT broker in the same process; no separate broker application is required.
- Generates a random MQTT token and stores it in Local AppData protected by Windows DPAPI for the current user.
- Displays each discovered device's name, address, type, pairing state, connection state, and signal strength when Windows provides those values.
- Lets an operator select a discovered BLE device, enter its passkey, pair it, validate the firmware's authenticated GATT service, and provision the saved network over encrypted BLE.
- Provisions the selected Wi-Fi network and local MQTT endpoint over the encrypted BLE connection.
- Waits for the Robobooth to connect to both Wi-Fi and MQTT before reporting the combined setup as connected.
- Provides a `/devices` page listing the currently connected robots and a button to open each programming workspace.
- Lets desktop users collapse the navigation sidebar to icon-only mode, with text tooltips for every navigation icon.
- Opens a Blockly editor from each live device and saves multiple named workspaces for that device on the server.
- Compiles the active Blockly workspace, deploys it over MQTT, and starts it on the robot without rebooting firmware.
- Provides admin-only, per-robot XtermBlazor terminals for bounded diagnostic logs reported over authenticated MQTT.
- Provides an admin-only saved-program manager for downloading workspace JSON backups and removing stored files.
- Provides an admin-only firmware flasher that discovers Windows COM ports, verifies PlatformIO CLI availability,
  and streams build/upload output while flashing the checked-in ESP32-S3 firmware.
- Provides the complete MakeCode-inspired robot block catalog, with common hardware blocks first and raw GPIO/I2C
  configuration isolated in an Advanced category.
- Shows the latest validated distance, colour/light, five-channel line, motor encoder, and servo values beside the
  programming workspace when firmware publishes the v1 sensor snapshot.
- Synchronizes Blockly edits between everyone programming the same robot, including a live editor list and colored block selections.
- Gives each browser a persistent random adjective-and-animal identity and color, with an option to choose a custom display name.
- Keeps the selected robot connection in a process-wide singleton and asks Windows to maintain and automatically restore the GATT connection.

The browser does not access Bluetooth directly. Discovery is cancelled when the Bluetooth page is disposed, but an established connection is owned by the server process and remains active when the operator navigates away or closes the browser. Explicit disconnect removes the device from Windows paired devices. An orderly server-process shutdown does the same automatically.

## Requirements

- Windows 10 version 2004 or newer, or Windows 11
- .NET 10 SDK
- A working Bluetooth adapter enabled in Windows
- The Bluetooth Support Service available on the host

The project targets `net10.0-windows10.0.19041.0` because Bluetooth discovery uses Windows Runtime APIs and cannot run on Linux or macOS as currently written.

## Build and run

From the repository root:

```powershell
dotnet restore src/RobotCompetitionBooth.slnx
dotnet build src/RobotCompetitionBooth.slnx
dotnet run --project src/RobotCompetitionBooth.Web
```

The default development addresses are:

- `http://localhost:5107`
- `https://localhost:7238`

### Publish one Windows executable

The checked-in `WindowsSingleFile` profile packages the app, embedded MQTT broker, .NET runtime, configuration,
and static web assets into one self-contained x64 executable:

```powershell
dotnet publish src/RobotCompetitionBooth.Web -p:PublishProfile=WindowsSingleFile
```

The result is `src/RobotCompetitionBooth.Web/bin/publish/win-x64/RobotCompetitionBooth.Web.exe`.
Start that executable and open `http://localhost:5000` unless `ASPNETCORE_URLS` or another hosting setting changes
the HTTP address. Windows may ask for firewall access the first time because the built-in MQTT broker listens for
Robobooth devices on the local network.

Open `/admin` or select **Admin** in the navigation, then enter the configured administrator password. The checked-in
default is `admin`; override it for the running server with the `Admin__Password` environment variable. Admin unlock
lasts only for that Blazor browser circuit and can be ended with **Lock admin**.

From Admin, open **Wi-Fi setup** (`/admin/wifi`). Windows scans the host computer's Wi-Fi adapter and lists visible networks by connection state and signal strength. The network currently connected to the computer is marked and always appears first. Select the Robobooth target, enter a passphrase only when the selected network is secured, and save it. Open networks are saved directly with no password. Any password is stored in a device-local file protected by Windows DPAPI for the current user and is never rendered back into the page.

On current Windows 11 versions, nearby Wi-Fi scanning can require Location services and the **Let desktop apps access your location** permission. If Windows denies access, the page explains which setting to enable and provides **Scan again**.

Next, return to Admin and open **Bluetooth setup** (`/admin/bluetooth`). The page starts an eight-second scan automatically and also provides a **Scan again** button. Device selection and connection remain disabled until Wi-Fi setup is complete.

To connect to the ESP32-S3:

1. Power the board and confirm that its RGB LED is green.
2. Scan and select the `RobotBooth-ESP32S3` BLE device. If Windows has not received its name yet, select the matching nearby BLE address.
3. Enter `123`; the server normalizes it to the six-digit BLE passkey `000123`.
4. Select **Pair and connect**.
5. The server sends the saved Wi-Fi configuration and authenticated local MQTT endpoint through GATT writes split into minimum-MTU-safe packets.
6. Wait for the Robobooth to join that network, connect to MQTT, and for the server connection card to show **Connected**.
7. Open **Live devices** to see the robot in the connected-device list.
8. Select **Program device** to create or reopen its Blockly workspaces. Enter a file name and select **Save** to
   write `%LOCALAPPDATA%\RobotCompetitionBooth\device-programs\<device-id>\<workspace-name>.json` on the server
   computer. **Load workspace** lists only the workspaces saved for that device. Everyone currently programming
   that robot shares live Blockly edits and can see the other editors' selected blocks. A private/incognito window
   receives a separate generated identity, and **Change my name** replaces the generated display name for that browser.

**Run on robot** saves and compiles the current workspace, deploys the instruction package through MQTT, and starts
it without restarting the ESP32. Firmware diagnostic messages use each authenticated robot's
`telemetry/logs` MQTT topic and are retained in separate bounded in-memory buffers on the server. From Admin,
**Robot terminal** (`/admin/terminal`) lists known robots and opens a terminal scoped to one device ID. USB serial
output is not displayed in these terminals. **Saved programs** (`/admin/programs`) lists workspace files across all
device IDs and allows an administrator to download or permanently remove them. The Blockly toolbox does not expose
console or logging commands to robot programmers.

The internal serial reader defaults to `COM5` at `115200` baud and is not exposed through the website. Change
`RobotSerial:PortName` and `RobotSerial:BaudRate`, or set `RobotSerial:Enabled` to `false`, when the server computer
uses a different USB setup.

From Admin, **Firmware flashing** (`/admin/firmware`) lists COM ports currently reported by Windows. Flashing is
disabled when the PlatformIO CLI or firmware project is unavailable. After confirmation, the server temporarily
releases the selected port from USB serial logging, runs the configured PlatformIO environment, streams its output,
and resumes serial logging when the upload succeeds, fails, times out, or is cancelled. Only one flash can run at a
time. The default settings use the `pio` executable, environment `esp32-s3-n16r8`, and a ten-minute timeout; override
them under `FirmwareFlashing` in configuration when necessary. Published builds include a copy of the firmware
project beside the application, but PlatformIO and its toolchain must still be installed on the server computer.

The connection is accepted only after the server finds the firmware's custom service, securely reads the status characteristic value `ready`, provisions Wi-Fi and MQTT, and reads back the `mqtt-connected` status.

## Embedded MQTT notes

- The broker is a background service inside `RobotCompetitionBooth.Web.exe` and defaults to TCP port `1883` on all local interfaces.
- Devices authenticate with a generated token that is never shown in the UI or application logs. It is sent only during encrypted, authenticated BLE provisioning and kept in ESP32 RAM.
- The advertised broker address is selected from the host's active LAN IPv4 adapters. If the wrong adapter is selected, set `EmbeddedMqtt:AdvertisedHost` in configuration to the computer's Wi-Fi IPv4 address.
- The MQTT connection is authenticated but is not TLS-encrypted. Use it only on a trusted booth LAN; the web app itself should likewise not be exposed to an untrusted network.
- Allow inbound TCP port `1883` through Windows Firewall for the private network used by the booth.

To bind an explicit local address:

```powershell
dotnet run --project src/RobotCompetitionBooth.Web --urls http://127.0.0.1:5127
```

## Bluetooth notes

- Bluetooth operations happen on the server computer. A remote visitor sees devices near the server, not devices near their phone or laptop.
- Generic BLE peripherals do not necessarily appear in a phone's normal Bluetooth settings. A BLE application such as nRF Connect can inspect custom BLE advertisements and GATT services.
- Device names and pairing state are supplied by Windows and may initially appear as unknown.
- Only one scan runs at a time inside a server process.
- Only one robot connection is managed per server process. Connecting another device replaces the current connection.
- Wi-Fi credentials are retrieved only while establishing a robot connection and are not written to application logs.
- BLE Wi-Fi provisioning uses authenticated and encrypted GATT characteristics. The password is split into write-only packets and is never exposed by a readable characteristic.
- The entered pairing code is used only during the pairing call and is not retained by the singleton service.
- Windows retains the bond while the managed connection is active, so it may not request the code again during automatic reconnection. The app removes that bond on disconnect, post-pairing failure, or orderly shutdown.
- **Disconnect and forget** closes the GATT session and removes the current Robobooth bond from Windows. The app also performs this cleanup after a failed post-pairing connection and during orderly host shutdown.
- A new connection removes any pre-existing Robobooth bond first, preventing Windows from reusing a stale GATT-service cache after firmware updates or an ungraceful shutdown.
- Application startup sweeps any saved `RobotBooth-*` BLE bonds left by an earlier forced or interrupted process exit. Disconnect and orderly shutdown perform the same Windows-wide sweep.
- `GattSession.MaintainConnection` lets Windows reconnect when a paired board returns to range. It does not persist the live connection across a website process restart.
- Wi-Fi and Bluetooth setup require the circuit-scoped admin password, but this is not a replacement for full user authentication, HTTPS, or rate limiting. Change the default password and do not expose the website to an untrusted network.
- The ESP32 firmware advertises as `RobotBooth-ESP32S3` and exposes its custom service over BLE.

## Important source files

- `Program.cs` configures dependency injection and the Blazor Server application.
- `Components/Pages/Bluetooth.razor` contains the discovery page.
- `Services/BluetoothDiscoveryService.cs` wraps the Windows Bluetooth discovery APIs.
- `Services/BluetoothConnectionManager.cs` owns the process-wide pairing, GATT validation, and maintained connection.
- `Services/WifiCredentialStore.cs` validates Wi-Fi settings and protects their device-local storage with Windows DPAPI.
- `Services/WifiNetworkScanner.cs` uses the Windows Native Wi-Fi API to scan the host adapter and identify its current connection.
- `Components/Pages/Wifi.razor` contains the Wi-Fi setup page.
- `Services/EmbeddedMqttBrokerService.cs` hosts and authenticates the in-process MQTT broker.
- `Services/RobotDeviceStateService.cs` owns the current colour and connection state for each MQTT device.
- `Models/RobotSensorSnapshot.cs` mirrors the validated MQTT sensor-snapshot contract used by the programming UI.
- `Components/Pages/Devices.razor` renders the live device colour page.
- `Components/Pages/DeviceProgram.razor` hosts the Blockly editor for one device.
- `Components/Pages/AdminTerminal.razor` lists the per-robot terminals behind admin access.
- `Components/Pages/AdminRobotTerminal.razor` hosts one admin-only robot diagnostic terminal.
- `Components/Pages/AdminFirmware.razor` provides the admin-only COM-port selection and firmware-flashing UI.
- `Components/RobotTelemetryTerminal.razor` renders one robot's validated MQTT diagnostic log through XtermBlazor.
- `Components/Pages/AdminPrograms.razor` manages saved workspace downloads and removal.
- `Components/Admin/AdminGate.razor` renders the password gate used by every admin setup route.
- `Services/AdminAccessService.cs` verifies the configured password and holds the current circuit's unlock state.
- `Services/DeviceProgramStore.cs` validates, lists, and atomically saves named workspace JSON files per device.
- `Services/RobotDiagnosticLogService.cs` keeps a separate bounded diagnostic-log buffer for every robot.
- `Services/RobotSerialTerminalService.cs` internally monitors the configured serial port and releases it for flashing.
- `Services/FirmwareFlashingService.cs` discovers COM ports and runs the fixed PlatformIO upload workflow.
- `Services/RobotCollaborationService.cs` owns the process-wide live workspace and editor presence for each device.
- `Properties/PublishProfiles/WindowsSingleFile.pubxml` defines the self-contained single-executable deployment.
- `Models/BluetoothDeviceInfo.cs` contains the discovery result models.
- `Models/BluetoothConnectionState.cs` contains the shared connection status shown to every Blazor circuit.
