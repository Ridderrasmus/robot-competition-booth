# Robot Competition Booth Web

Interactive Blazor Server application for discovering and managing the robot from the Windows computer hosting the website.

## Current behavior

- Runs interactive Razor components on the server.
- Provides a `/bluetooth` page that scans for Bluetooth Classic and Bluetooth Low Energy devices.
- Uses the Windows Bluetooth adapter attached to the server computer.
- Provides a separate `/wifi` setup page that scans with the host's Windows Wi-Fi adapter, pins and marks the host's current connection, and supports secured and open networks.
- Stores the selected network credentials in Local AppData encrypted with Windows DPAPI for the current user.
- Blocks Robobooth BLE connections until Wi-Fi credentials have been configured.
- Hosts an authenticated MQTT broker in the same process; no separate broker application is required.
- Generates a random MQTT token and stores it in Local AppData protected by Windows DPAPI for the current user.
- Displays each discovered device's name, address, type, pairing state, connection state, and signal strength when Windows provides those values.
- Lets an operator select a discovered BLE device, enter its passkey, pair it, validate the firmware's authenticated GATT service, and provision the saved network over encrypted BLE.
- Provisions the selected Wi-Fi network and local MQTT endpoint over the encrypted BLE connection.
- Waits for the Robobooth to connect to both Wi-Fi and MQTT before reporting the combined setup as connected.
- Provides a `/devices` page showing each MQTT-connected device, its live RGB value, and the last update time.
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

Open `/wifi` or select **Wi-Fi setup** in the navigation. Windows scans the host computer's Wi-Fi adapter and lists visible networks by connection state and signal strength. The network currently connected to the computer is marked and always appears first. Select the Robobooth target, enter a passphrase only when the selected network is secured, and save it. Open networks are saved directly with no password. Any password is stored in a device-local file protected by Windows DPAPI for the current user and is never rendered back into the page.

On current Windows 11 versions, nearby Wi-Fi scanning can require Location services and the **Let desktop apps access your location** permission. If Windows denies access, the page explains which setting to enable and provides **Scan again**.

Next, open `/bluetooth` or select **Bluetooth**. The page starts an eight-second scan automatically and also provides a **Scan again** button. Device selection and connection remain disabled until Wi-Fi setup is complete.

To connect to the ESP32-S3:

1. Power the board and confirm that its RGB LED is green.
2. Scan and select the `RobotBooth-ESP32S3` BLE device. If Windows has not received its name yet, select the matching nearby BLE address.
3. Enter `123`; the server normalizes it to the six-digit BLE passkey `000123`.
4. Select **Pair and connect**.
5. The server sends the saved Wi-Fi configuration and authenticated local MQTT endpoint through GATT writes split into minimum-MTU-safe packets.
6. Wait for the Robobooth to join that network, connect to MQTT, and for the server connection card to show **Connected**.
7. Open **Live devices** to see its slowly changing colour update once per second.

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
- The website currently has no authentication or authorization. Do not expose it to untrusted networks while Bluetooth management actions are available to every visitor.
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
- `Components/Pages/Devices.razor` renders the live device colour page.
- `Properties/PublishProfiles/WindowsSingleFile.pubxml` defines the self-contained single-executable deployment.
- `Models/BluetoothDeviceInfo.cs` contains the discovery result models.
- `Models/BluetoothConnectionState.cs` contains the shared connection status shown to every Blazor circuit.
