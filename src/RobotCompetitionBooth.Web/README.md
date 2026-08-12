# Robot Competition Booth Web

Interactive Blazor Server application for discovering and managing the robot from the Windows computer hosting the website.

## Current behavior

- Runs interactive Razor components on the server.
- Provides a `/bluetooth` page that scans for Bluetooth Classic and Bluetooth Low Energy devices.
- Uses the Windows Bluetooth adapter attached to the server computer.
- Displays each discovered device's name, address, type, pairing state, connection state, and signal strength when Windows provides those values.
- Lets an operator select a discovered BLE device, enter its passkey, pair it, and connect to the firmware's authenticated GATT service.
- Keeps the selected robot connection in a process-wide singleton and asks Windows to maintain and automatically restore the GATT connection.

The browser does not access Bluetooth directly. Discovery is cancelled when the Bluetooth page is disposed, but an established connection is owned by the server process and remains active when the operator navigates away or closes the browser. It ends only when explicitly disconnected or when the server process stops.

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

Open `/bluetooth` or select **Bluetooth** in the navigation. The page starts an eight-second scan automatically and also provides a **Scan again** button.

To connect to the ESP32-S3:

1. Power the board and confirm that its RGB LED is green.
2. Scan and select the `RobotBooth-ESP32S3` BLE device. If Windows has not received its name yet, select the matching nearby BLE address.
3. Enter `123`; the server normalizes it to the six-digit BLE passkey `000123`.
4. Select **Pair and connect**.
5. Wait for the server connection card to show **Connected**.

The connection is accepted only after the server finds the firmware's custom service and securely reads the status characteristic value `ready`.

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
- The entered pairing code is used only during the pairing call and is not retained by the singleton service.
- Windows retains successful bonds. The page still requires a code before connecting, but Windows may not request it again until the device is removed from Windows Bluetooth settings.
- `GattSession.MaintainConnection` lets Windows reconnect when a paired board returns to range. It does not persist the live connection across a website process restart.
- The website currently has no authentication or authorization. Do not expose it to untrusted networks while Bluetooth management actions are available to every visitor.
- The ESP32 firmware advertises as `RobotBooth-ESP32S3` and exposes its custom service over BLE.

## Important source files

- `Program.cs` configures dependency injection and the Blazor Server application.
- `Components/Pages/Bluetooth.razor` contains the discovery page.
- `Services/BluetoothDiscoveryService.cs` wraps the Windows Bluetooth discovery APIs.
- `Services/BluetoothConnectionManager.cs` owns the process-wide pairing, GATT validation, and maintained connection.
- `Models/BluetoothDeviceInfo.cs` contains the discovery result models.
- `Models/BluetoothConnectionState.cs` contains the shared connection status shown to every Blazor circuit.
