using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using RobotCompetitionBooth.Web.Models;

namespace RobotCompetitionBooth.Web.Services;

public sealed class BluetoothConnectionManager(
    ILogger<BluetoothConnectionManager> logger) : IAsyncDisposable
{
    private static readonly Guid RobotServiceUuid =
        Guid.Parse("8ddf7a40-7520-4e57-9e32-9b6b091c5c8b");
    private static readonly Guid StatusCharacteristicUuid =
        Guid.Parse("f6eb0c76-11a6-4a5f-a58d-6b55d94ff31a");

    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private readonly object stateLock = new();
    private BluetoothConnectionState state = BluetoothConnectionState.Initial;
    private BluetoothLEDevice? connectedDevice;
    private GattSession? gattSession;
    private GattDeviceService? robotService;
    private bool disposed;

    public event EventHandler<BluetoothConnectionState>? StateChanged;

    public BluetoothConnectionState State
    {
        get
        {
            lock (stateLock)
            {
                return state;
            }
        }
    }

    public async Task<BluetoothConnectionResult> ConnectAsync(
        BluetoothDeviceInfo device,
        string pairingCode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!device.IsLowEnergy)
        {
            return new(false, "Only Bluetooth Low Energy devices can be connected by this page.");
        }

        if (!TryNormalizePairingCode(pairingCode, out var normalizedCode))
        {
            return new(false, "Enter a numeric pairing code containing one to six digits.");
        }

        if (!TryParseAddress(device.Address, out var bluetoothAddress))
        {
            return new(false, "Windows did not provide a usable Bluetooth address for this device.");
        }

        await connectionLock.WaitAsync();
        BluetoothLEDevice? candidateDevice = null;
        GattSession? candidateSession = null;
        GattDeviceService? candidateService = null;
        var connectionStage = "Opening the BLE device";

        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var currentState = State;
            if (currentState.IsConnected &&
                string.Equals(currentState.DeviceAddress, device.Address, StringComparison.OrdinalIgnoreCase))
            {
                return new(true, $"{DisplayName(device)} is already connected.");
            }

            DisposeConnectionResources();
            Publish(new(
                BluetoothConnectionPhase.Pairing,
                DisplayName(device),
                device.Address,
                $"Preparing to pair with {DisplayName(device)}...",
                DateTimeOffset.Now));

            candidateDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            if (candidateDevice is null)
            {
                throw new InvalidOperationException(
                    "Windows could not open this BLE device. Scan again and select its newest entry.");
            }

            connectionStage = "Pairing with the BLE device";
            var pairingResult = await PairIfNeededAsync(candidateDevice, normalizedCode);
            if (!pairingResult.Succeeded)
            {
                throw new InvalidOperationException(pairingResult.Message);
            }

            Publish(new(
                BluetoothConnectionPhase.Connecting,
                DisplayName(device),
                device.Address,
                "Paired. Connecting to the robot GATT service...",
                DateTimeOffset.Now));

            connectionStage = "Creating the maintained GATT session";
            candidateSession = await GattSession.FromDeviceIdAsync(candidateDevice.BluetoothDeviceId);
            if (candidateSession is null || !candidateSession.CanMaintainConnection)
            {
                throw new InvalidOperationException(
                    "Windows cannot maintain a persistent GATT connection to this device.");
            }

            connectionStage = "Discovering the robot GATT service";
            // Pairing populates the Windows GATT cache. Cached discovery avoids an
            // adapter-specific ERROR_BAD_COMMAND; the uncached read below still
            // proves that the selected board is live and authenticated.
            var servicesResult = await candidateDevice.GetGattServicesForUuidAsync(
                RobotServiceUuid,
                BluetoothCacheMode.Cached);
            if (servicesResult.Status != GattCommunicationStatus.Success ||
                servicesResult.Services.Count == 0)
            {
                throw new InvalidOperationException(
                    "The selected device does not expose the Robot Competition Booth service, or it became unreachable.");
            }

            candidateService = servicesResult.Services[0];
            foreach (var unusedService in servicesResult.Services.Skip(1))
            {
                unusedService.Dispose();
            }

            connectionStage = "Discovering the secure status characteristic";
            var characteristicsResult = await candidateService.GetCharacteristicsForUuidAsync(
                StatusCharacteristicUuid,
                BluetoothCacheMode.Cached);
            if (characteristicsResult.Status != GattCommunicationStatus.Success ||
                characteristicsResult.Characteristics.Count == 0)
            {
                throw new InvalidOperationException(
                    "The robot status characteristic could not be found after pairing.");
            }

            connectionStage = "Reading the secure robot status";
            var readResult = await characteristicsResult.Characteristics[0].ReadValueAsync(
                BluetoothCacheMode.Uncached);
            if (readResult.Status != GattCommunicationStatus.Success)
            {
                throw new InvalidOperationException(
                    $"The secure robot status could not be read ({readResult.Status}). Check the pairing code and try again.");
            }

            var robotStatus = ReadUtf8(readResult.Value);
            if (!string.Equals(robotStatus, "ready", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The selected device answered with an unexpected robot status value.");
            }

            connectionStage = "Enabling the maintained GATT connection";
            candidateSession.MaintainConnection = true;

            connectedDevice = candidateDevice;
            candidateDevice = null;
            gattSession = candidateSession;
            candidateSession = null;
            robotService = candidateService;
            candidateService = null;
            connectedDevice.ConnectionStatusChanged += OnConnectionStatusChanged;

            var connectedName = string.IsNullOrWhiteSpace(connectedDevice.Name)
                ? DisplayName(device)
                : connectedDevice.Name;
            Publish(new(
                BluetoothConnectionPhase.Connected,
                connectedName,
                device.Address,
                $"Connected to {connectedName}. Windows will maintain this connection for the lifetime of the server process.",
                DateTimeOffset.Now));

            logger.LogInformation(
                "Connected to BLE robot {DeviceName} at {DeviceAddress}",
                connectedName,
                device.Address);
            return new(true, $"Connected to {connectedName}.");
        }
        catch (Exception exception)
        {
            candidateService?.Dispose();
            if (candidateSession is not null)
            {
                TrySetMaintainConnection(candidateSession, false);
                candidateSession.Dispose();
            }

            candidateDevice?.Dispose();
            DisposeConnectionResources();

            var message = $"{connectionStage} failed: {CleanErrorMessage(exception)}";
            Publish(new(
                BluetoothConnectionPhase.Failed,
                DisplayName(device),
                device.Address,
                message,
                DateTimeOffset.Now));
            logger.LogWarning(
                exception,
                "Failed to connect to BLE device {DeviceName} at {DeviceAddress}",
                DisplayName(device),
                device.Address);
            return new(false, message);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await connectionLock.WaitAsync();

        try
        {
            var current = State;
            Publish(current with
            {
                Phase = BluetoothConnectionPhase.Disconnecting,
                StatusMessage = "Disconnecting from the robot...",
                LastChanged = DateTimeOffset.Now
            });

            DisposeConnectionResources();
            Publish(BluetoothConnectionState.Initial with
            {
                LastChanged = DateTimeOffset.Now
            });
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private static async Task<BluetoothConnectionResult> PairIfNeededAsync(
        BluetoothLEDevice device,
        string pairingCode)
    {
        var pairing = device.DeviceInformation.Pairing;
        if (pairing.IsPaired)
        {
            return new(true, "The device is already paired.");
        }

        if (!pairing.CanPair)
        {
            return new(false, "Windows reports that this device cannot currently be paired.");
        }

        var customPairing = pairing.Custom;
        void PairingRequested(
            DeviceInformationCustomPairing _,
            DevicePairingRequestedEventArgs args)
        {
            if (args.PairingKind == DevicePairingKinds.ProvidePin)
            {
                args.Accept(pairingCode);
            }
        }

        customPairing.PairingRequested += PairingRequested;
        try
        {
            var result = await customPairing.PairAsync(
                DevicePairingKinds.ProvidePin,
                DevicePairingProtectionLevel.EncryptionAndAuthentication);
            return result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired
                ? new(true, "Pairing succeeded.")
                : new(false, $"Pairing failed: {FormatPairingStatus(result.Status)}.");
        }
        finally
        {
            customPairing.PairingRequested -= PairingRequested;
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        var current = State;
        if (!ReferenceEquals(sender, connectedDevice))
        {
            return;
        }

        var isConnected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        Publish(current with
        {
            Phase = isConnected
                ? BluetoothConnectionPhase.Connected
                : BluetoothConnectionPhase.Reconnecting,
            StatusMessage = isConnected
                ? $"Connected to {current.DeviceName}."
                : $"{current.DeviceName} is out of range. Windows will reconnect when it becomes available.",
            LastChanged = DateTimeOffset.Now
        });
    }

    private void DisposeConnectionResources()
    {
        if (connectedDevice is not null)
        {
            connectedDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
        }

        robotService?.Dispose();
        robotService = null;

        if (gattSession is not null)
        {
            TrySetMaintainConnection(gattSession, false);
            gattSession.Dispose();
            gattSession = null;
        }

        connectedDevice?.Dispose();
        connectedDevice = null;
    }

    private void Publish(BluetoothConnectionState newState)
    {
        lock (stateLock)
        {
            state = newState;
        }

        StateChanged?.Invoke(this, newState);
    }

    private static bool TryNormalizePairingCode(string pairingCode, out string normalizedCode)
    {
        var trimmed = pairingCode?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 6 || trimmed.Any(character => !char.IsAsciiDigit(character)))
        {
            normalizedCode = string.Empty;
            return false;
        }

        normalizedCode = trimmed.PadLeft(6, '0');
        return true;
    }

    private static bool TryParseAddress(string address, out ulong bluetoothAddress)
    {
        var compact = new string(address.Where(Uri.IsHexDigit).ToArray());
        bluetoothAddress = 0;
        return compact.Length == 12 && ulong.TryParse(
            compact,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out bluetoothAddress);
    }

    private static string ReadUtf8(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        return reader.ReadString(reader.UnconsumedBufferLength);
    }

    private static string DisplayName(BluetoothDeviceInfo device) =>
        device.Name == "Unknown device" ? device.Address : device.Name;

    private static string CleanErrorMessage(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? $"{exception.GetType().Name} (0x{exception.HResult:X8})"
            : exception.Message;

    private static bool TrySetMaintainConnection(GattSession session, bool maintainConnection)
    {
        try
        {
            session.MaintainConnection = maintainConnection;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatPairingStatus(DevicePairingResultStatus status) => status switch
    {
        DevicePairingResultStatus.AuthenticationFailure => "the code was rejected",
        DevicePairingResultStatus.AuthenticationTimeout => "authentication timed out",
        DevicePairingResultStatus.ConnectionRejected => "the device rejected the connection",
        DevicePairingResultStatus.NotReadyToPair => "the device is not ready to pair",
        DevicePairingResultStatus.TooManyConnections => "the device has too many active connections",
        _ => status.ToString()
    };

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await connectionLock.WaitAsync();
        try
        {
            DisposeConnectionResources();
        }
        finally
        {
            connectionLock.Release();
            connectionLock.Dispose();
        }
    }
}
