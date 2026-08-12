# Robot Competition Booth Web

Interactive Blazor Server application for discovering and managing the robot from the Windows computer hosting the website.

## Current behavior

- Runs interactive Razor components on the server.
- Provides a `/bluetooth` page that scans for Bluetooth Classic and Bluetooth Low Energy devices.
- Uses the Windows Bluetooth adapter attached to the server computer.
- Displays each discovered device's name, address, type, pairing state, connection state, and signal strength when Windows provides those values.

The browser does not access Bluetooth directly. Closing a browser tab does not turn off the server's Bluetooth adapter, but page-scoped work in the current implementation is cancelled when that page is disposed.

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

To bind an explicit local address:

```powershell
dotnet run --project src/RobotCompetitionBooth.Web --urls http://127.0.0.1:5127
```

## Bluetooth notes

- Bluetooth operations happen on the server computer. A remote visitor sees devices near the server, not devices near their phone or laptop.
- Generic BLE peripherals do not necessarily appear in a phone's normal Bluetooth settings. A BLE application such as nRF Connect can inspect custom BLE advertisements and GATT services.
- Device names and pairing state are supplied by Windows and may initially appear as unknown.
- Only one scan runs at a time inside a server process.
- The ESP32 firmware advertises as `RobotBooth-ESP32S3` and exposes its custom service over BLE.

## Important source files

- `Program.cs` configures dependency injection and the Blazor Server application.
- `Components/Pages/Bluetooth.razor` contains the discovery page.
- `Services/BluetoothDiscoveryService.cs` wraps the Windows Bluetooth discovery APIs.
- `Models/BluetoothDeviceInfo.cs` contains the discovery result models.
