using System.Collections.Concurrent;
using RobotCompetitionBooth.Web.Models;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace RobotCompetitionBooth.Web.Services;

public sealed class BluetoothDiscoveryService
{
    private const string BluetoothProtocolSelector =
        "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";
    private const string AddressProperty = "System.Devices.Aep.DeviceAddress";
    private const string ConnectedProperty = "System.Devices.Aep.IsConnected";
    private const string PairedProperty = "System.Devices.Aep.IsPaired";
    private const string PresentProperty = "System.Devices.Aep.IsPresent";

    private static readonly string[] RequestedProperties =
    [
        AddressProperty,
        ConnectedProperty,
        PairedProperty,
        PresentProperty
    ];

    private readonly SemaphoreSlim scanLock = new(1, 1);

    public async Task<BluetoothScanResult> ScanAsync(
        TimeSpan duration,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await scanLock.WaitAsync(cancellationToken);

        try
        {
            return await ScanCoreAsync(duration, progress, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable("The server does not have permission to use Bluetooth.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable($"Bluetooth discovery failed: {exception.Message}");
        }
        finally
        {
            scanLock.Release();
        }
    }

    private static async Task<BluetoothScanResult> ScanCoreAsync(
        TimeSpan duration,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Preparing Bluetooth discovery on the server...");
        var devices = new ConcurrentDictionary<string, BluetoothDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        var deviceWatcher = CreateDeviceWatcher(devices);
        var advertisementWatcher = CreateAdvertisementWatcher(devices);

        try
        {
            progress?.Report("Starting Windows Bluetooth discovery...");
            deviceWatcher.Start();
            advertisementWatcher.Start();

            if (advertisementWatcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
            {
                return Unavailable("Windows could not start Bluetooth Low Energy discovery.");
            }

            progress?.Report($"Listening for nearby devices for {duration.TotalSeconds:0} seconds...");
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            progress?.Report("Finishing the Bluetooth scan...");
            Stop(deviceWatcher);
            Stop(advertisementWatcher);
        }

        var results = devices.Values
            .OrderBy(device => device.Name == "Unknown device")
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(device => device.SignalStrength)
            .ToArray();

        var message = results.Length == 0
            ? "No devices were found. Make sure nearby devices are powered on and discoverable."
            : $"Found {results.Length} Bluetooth device{(results.Length == 1 ? string.Empty : "s")}.";

        return new BluetoothScanResult(results, true, message);
    }

    private static DeviceWatcher CreateDeviceWatcher(
        ConcurrentDictionary<string, BluetoothDeviceInfo> devices)
    {
        var watcher = DeviceInformation.CreateWatcher(
            BluetoothProtocolSelector,
            RequestedProperties,
            DeviceInformationKind.AssociationEndpoint);

        watcher.Added += (_, device) => AddOrUpdateKnownDevice(devices, device);
        return watcher;
    }

    private static BluetoothLEAdvertisementWatcher CreateAdvertisementWatcher(
        ConcurrentDictionary<string, BluetoothDeviceInfo> devices)
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, advertisement) =>
        {
            var address = FormatAddress(advertisement.BluetoothAddress);
            var name = string.IsNullOrWhiteSpace(advertisement.Advertisement.LocalName)
                ? "Unknown device"
                : advertisement.Advertisement.LocalName.Trim();
            var discovered = new BluetoothDeviceInfo(
                address,
                name,
                address,
                null,
                null,
                true,
                advertisement.RawSignalStrengthInDBm,
                advertisement.Timestamp);

            devices.AddOrUpdate(address, discovered, (_, existing) => Merge(existing, discovered));
        };

        return watcher;
    }

    private static void AddOrUpdateKnownDevice(
        ConcurrentDictionary<string, BluetoothDeviceInfo> devices,
        DeviceInformation device)
    {
        var address = GetString(device, AddressProperty);
        var key = string.IsNullOrWhiteSpace(address) ? device.Id : NormalizeAddress(address);
        var discovered = new BluetoothDeviceInfo(
            device.Id,
            string.IsNullOrWhiteSpace(device.Name) ? "Unknown device" : device.Name.Trim(),
            string.IsNullOrWhiteSpace(address) ? "Unavailable" : NormalizeAddress(address),
            GetBoolean(device, PairedProperty) ?? device.Pairing.IsPaired,
            GetBoolean(device, ConnectedProperty),
            false,
            null,
            DateTimeOffset.Now);

        devices.AddOrUpdate(key, discovered, (_, existing) => Merge(existing, discovered));
    }

    private static BluetoothDeviceInfo Merge(BluetoothDeviceInfo existing, BluetoothDeviceInfo update)
    {
        var updateHasName = update.Name != "Unknown device";
        return existing with
        {
            Id = update.Id,
            Name = updateHasName ? update.Name : existing.Name,
            Address = update.Address == "Unavailable" ? existing.Address : update.Address,
            IsPaired = update.IsPaired ?? existing.IsPaired,
            IsConnected = update.IsConnected ?? existing.IsConnected,
            IsLowEnergy = existing.IsLowEnergy || update.IsLowEnergy,
            SignalStrength = update.SignalStrength ?? existing.SignalStrength,
            LastSeen = update.LastSeen > existing.LastSeen ? update.LastSeen : existing.LastSeen
        };
    }

    private static string? GetString(DeviceInformation device, string propertyName) =>
        device.Properties.TryGetValue(propertyName, out var value) ? value as string : null;

    private static bool? GetBoolean(DeviceInformation device, string propertyName) =>
        device.Properties.TryGetValue(propertyName, out var value) && value is bool flag ? flag : null;

    private static string FormatAddress(ulong address) => string.Join(
        ":",
        Enumerable.Range(0, 6)
            .Select(index => ((address >> ((5 - index) * 8)) & 0xff).ToString("X2")));

    private static string NormalizeAddress(string address)
    {
        var compact = new string(address.Where(Uri.IsHexDigit).ToArray());
        if (compact.Length != 12)
        {
            return address.ToUpperInvariant();
        }

        return string.Join(":", Enumerable.Range(0, 6).Select(index => compact.Substring(index * 2, 2)))
            .ToUpperInvariant();
    }

    private static void Stop(DeviceWatcher watcher)
    {
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            watcher.Stop();
        }
    }

    private static void Stop(BluetoothLEAdvertisementWatcher watcher)
    {
        if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
        {
            watcher.Stop();
        }
    }

    private static BluetoothScanResult Unavailable(string message) =>
        new([], false, message);
}
