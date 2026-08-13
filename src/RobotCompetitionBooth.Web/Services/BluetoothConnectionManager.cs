using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using RobotCompetitionBooth.Web.Models;

namespace RobotCompetitionBooth.Web.Services;

public sealed class BluetoothConnectionManager(
    WifiCredentialStore wifiCredentialStore,
    MqttBrokerEndpointProvider mqttEndpointProvider,
    ILogger<BluetoothConnectionManager> logger) : IAsyncDisposable
{
    private static readonly Guid RobotServiceUuid =
        Guid.Parse("8ddf7a40-7520-4e57-9e32-9b6b091c5c8b");
    private static readonly Guid StatusCharacteristicUuid =
        Guid.Parse("f6eb0c76-11a6-4a5f-a58d-6b55d94ff31a");
    private static readonly Guid WifiProvisioningCharacteristicUuid =
        Guid.Parse("0a7c0061-88a4-43cc-a010-faf7595da303");
    private static readonly Guid WifiStatusCharacteristicUuid =
        Guid.Parse("0a7c0062-88a4-43cc-a010-faf7595da303");

    private static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WifiStatusPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan UnpairRetryInterval = TimeSpan.FromMilliseconds(350);
    private const string RoboboothDeviceNamePrefix = "RobotBooth-";
    private const int UnpairAttempts = 3;
    private const byte ProvisioningProtocolVersion = 2;
    private const byte ProvisioningStartCommand = 1;
    private const byte ProvisioningDataCommand = 2;
    private const byte ProvisioningCommitCommand = 3;
    private const int ProvisioningChunkSize = 16;

    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private readonly CancellationTokenSource shutdownCancellation = new();
    private readonly object stateLock = new();
    private BluetoothConnectionState state = BluetoothConnectionState.Initial;
    private BluetoothLEDevice? connectedDevice;
    private GattSession? gattSession;
    private GattDeviceService? robotService;
    private DeviceInformation? pairedDeviceInformation;
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

        WifiCredentials? wifiCredentials;
        try
        {
            wifiCredentials = wifiCredentialStore.GetCredentials();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the encrypted Wi-Fi credentials");
            return new(false, "Windows could not read the saved Wi-Fi settings. Open Wi-Fi setup and save them again.");
        }

        if (wifiCredentials is null)
        {
            return new(false, "Configure Wi-Fi settings before connecting to a Robobooth device.");
        }

        MqttProvisioningSettings mqttSettings;
        try
        {
            mqttSettings = mqttEndpointProvider.GetProvisioningSettings();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not prepare the embedded MQTT provisioning settings");
            return new(false, $"The embedded MQTT connection could not be prepared: {CleanErrorMessage(exception)}");
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
        DeviceInformation? candidatePairingInformation = null;
        var connectionStage = "Opening the BLE device";

        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            shutdownCancellation.Token.ThrowIfCancellationRequested();

            var currentState = State;
            if (currentState.IsConnected &&
                string.Equals(currentState.DeviceAddress, device.Address, StringComparison.OrdinalIgnoreCase))
            {
                return new(true, $"{DisplayName(device)} is already connected.");
            }

            var previousDeviceCleanup = await DisconnectAndUnpairTrackedDeviceAsync();
            if (!previousDeviceCleanup.Succeeded)
            {
                throw new InvalidOperationException(previousDeviceCleanup.Message);
            }

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

            if (candidateDevice.DeviceInformation.Pairing.IsPaired)
            {
                connectionStage = "Removing the stale Windows pairing";
                var stalePairingInformation = candidateDevice.DeviceInformation;
                candidateDevice.Dispose();
                candidateDevice = null;

                var stalePairingCleanup = await TryUnpairAsync(stalePairingInformation);
                if (!stalePairingCleanup.Succeeded)
                {
                    pairedDeviceInformation = stalePairingInformation;
                    throw new InvalidOperationException(stalePairingCleanup.Message);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), shutdownCancellation.Token);
                candidateDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
                if (candidateDevice is null)
                {
                    throw new InvalidOperationException(
                        "Windows removed the previous pairing but could not reopen the BLE device. Scan again and retry.");
                }
            }

            connectionStage = "Pairing with the BLE device";
            var pairingResult = await PairIfNeededAsync(candidateDevice, normalizedCode);
            if (!pairingResult.Succeeded)
            {
                throw new InvalidOperationException(pairingResult.Message);
            }

            candidatePairingInformation = candidateDevice.DeviceInformation;

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

            connectionStage = "Discovering the secure Wi-Fi provisioning characteristic";
            var provisioningCharacteristicsResult = await candidateService.GetCharacteristicsForUuidAsync(
                WifiProvisioningCharacteristicUuid,
                BluetoothCacheMode.Cached);
            if (provisioningCharacteristicsResult.Status != GattCommunicationStatus.Success ||
                provisioningCharacteristicsResult.Characteristics.Count == 0)
            {
                throw new InvalidOperationException(
                    "The Robobooth firmware does not expose the secure Wi-Fi provisioning characteristic.");
            }

            connectionStage = "Discovering the secure Wi-Fi status characteristic";
            var wifiStatusCharacteristicsResult = await candidateService.GetCharacteristicsForUuidAsync(
                WifiStatusCharacteristicUuid,
                BluetoothCacheMode.Cached);
            if (wifiStatusCharacteristicsResult.Status != GattCommunicationStatus.Success ||
                wifiStatusCharacteristicsResult.Characteristics.Count == 0)
            {
                throw new InvalidOperationException(
                    "The Robobooth firmware does not expose the secure Wi-Fi status characteristic.");
            }

            Publish(new(
                BluetoothConnectionPhase.Provisioning,
                DisplayName(device),
                device.Address,
                $"Sending the saved Wi-Fi and embedded MQTT settings to the Robobooth...",
                DateTimeOffset.Now));

            connectionStage = "Provisioning the Robobooth Wi-Fi and MQTT connections";
            await ProvisionRobotAsync(
                provisioningCharacteristicsResult.Characteristics[0],
                wifiStatusCharacteristicsResult.Characteristics[0],
                wifiCredentials,
                mqttSettings,
                shutdownCancellation.Token);

            connectionStage = "Enabling the maintained GATT connection";
            candidateSession.MaintainConnection = true;

            connectedDevice = candidateDevice;
            candidateDevice = null;
            gattSession = candidateSession;
            candidateSession = null;
            robotService = candidateService;
            candidateService = null;
            pairedDeviceInformation = candidatePairingInformation;
            candidatePairingInformation = null;
            connectedDevice.ConnectionStatusChanged += OnConnectionStatusChanged;

            var connectedName = string.IsNullOrWhiteSpace(connectedDevice.Name)
                ? DisplayName(device)
                : connectedDevice.Name;
            Publish(new(
                BluetoothConnectionPhase.Connected,
                connectedName,
                device.Address,
                $"Connected to {connectedName}. Wi-Fi and the embedded MQTT link are ready. Windows will maintain the BLE connection for the lifetime of the server process.",
                DateTimeOffset.Now));

            logger.LogInformation(
                "Connected to BLE robot {DeviceName} at {DeviceAddress}",
                connectedName,
                device.Address);
            return new(true, $"Connected to {connectedName}. Live colour telemetry is ready.");
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

            var pairingCleanup = await TryUnpairAsync(candidatePairingInformation);
            if (!pairingCleanup.Succeeded && candidatePairingInformation is not null)
            {
                pairedDeviceInformation = candidatePairingInformation;
            }

            var message = $"{connectionStage} failed: {CleanErrorMessage(exception)}";
            if (!pairingCleanup.Succeeded)
            {
                message += $" {pairingCleanup.Message}";
            }

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

    public async Task<BluetoothConnectionResult> DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await connectionLock.WaitAsync();

        try
        {
            var current = State;
            Publish(current with
            {
                Phase = BluetoothConnectionPhase.Disconnecting,
                StatusMessage = "Disconnecting and removing the robot from Windows...",
                LastChanged = DateTimeOffset.Now
            });

            var result = await DisconnectAndUnpairTrackedDeviceAsync();
            if (result.Succeeded)
            {
                Publish(BluetoothConnectionState.Initial with
                {
                    StatusMessage = "The Bluetooth device was disconnected and removed from Windows.",
                    LastChanged = DateTimeOffset.Now
                });
            }
            else
            {
                Publish(current with
                {
                    Phase = BluetoothConnectionPhase.Failed,
                    StatusMessage = result.Message,
                    LastChanged = DateTimeOffset.Now
                });
            }

            return result;
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

    private async Task<BluetoothConnectionResult> DisconnectAndUnpairTrackedDeviceAsync()
    {
        var deviceInformation = pairedDeviceInformation ?? connectedDevice?.DeviceInformation;
        DisposeConnectionResources();

        var result = await RemoveSavedRoboboothDevicesAsync(deviceInformation);
        if (result.Succeeded)
        {
            pairedDeviceInformation = null;
        }
        else if (deviceInformation is not null)
        {
            // Keep the reference so a later disconnect or host shutdown can retry.
            pairedDeviceInformation = deviceInformation;
        }

        return result;
    }

    private async Task<BluetoothConnectionResult> TryUnpairAsync(DeviceInformation? deviceInformation)
    {
        if (deviceInformation is null || !deviceInformation.Pairing.IsPaired)
        {
            return new(true, "The Bluetooth device is not paired.");
        }

        try
        {
            DeviceUnpairingResultStatus? lastStatus = null;
            for (var attempt = 1; attempt <= UnpairAttempts; attempt++)
            {
                var result = await deviceInformation.Pairing.UnpairAsync();
                lastStatus = result.Status;
                if (result.Status is DeviceUnpairingResultStatus.Unpaired or
                    DeviceUnpairingResultStatus.AlreadyUnpaired)
                {
                    logger.LogInformation(
                        "Removed BLE device {DeviceName} from Windows paired devices",
                        deviceInformation.Name);
                    return new(true, "The Bluetooth device was removed from Windows paired devices.");
                }

                if (attempt < UnpairAttempts &&
                    result.Status is DeviceUnpairingResultStatus.OperationAlreadyInProgress or
                        DeviceUnpairingResultStatus.Failed)
                {
                    await Task.Delay(UnpairRetryInterval);
                    continue;
                }

                break;
            }

            return new(
                false,
                $"Windows could not remove the Bluetooth device from paired devices: {FormatUnpairingStatus(lastStatus ?? DeviceUnpairingResultStatus.Failed)}.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not remove BLE device {DeviceName} from Windows paired devices",
                deviceInformation.Name);
            return new(
                false,
                $"Windows could not remove the Bluetooth device from paired devices: {CleanErrorMessage(exception)}");
        }
    }

    private async Task<BluetoothConnectionResult> RemoveSavedRoboboothDevicesAsync(
        DeviceInformation? trackedDevice)
    {
        var failures = new List<string>();
        var attemptedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (trackedDevice is not null)
        {
            attemptedDeviceIds.Add(trackedDevice.Id);
            var trackedResult = await TryUnpairAsync(trackedDevice);
            if (!trackedResult.Succeeded)
            {
                failures.Add(trackedResult.Message);
            }
        }

        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var pairedDevices = await DeviceInformation.FindAllAsync(
                selector,
                Array.Empty<string>(),
                DeviceInformationKind.AssociationEndpoint);

            foreach (var pairedDevice in pairedDevices.Where(IsRoboboothDevice))
            {
                if (!attemptedDeviceIds.Add(pairedDevice.Id))
                {
                    continue;
                }

                var result = await TryUnpairAsync(pairedDevice);
                if (!result.Succeeded)
                {
                    failures.Add(result.Message);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not enumerate saved Robobooth Bluetooth devices");
            failures.Add(
                $"Windows could not enumerate saved Robobooth Bluetooth devices: {CleanErrorMessage(exception)}");
        }

        return failures.Count == 0
            ? new(true, "All saved Robobooth Bluetooth devices were removed from Windows.")
            : new(false, string.Join(" ", failures.Distinct(StringComparer.Ordinal)));
    }

    private static bool IsRoboboothDevice(DeviceInformation deviceInformation) =>
        deviceInformation.Name.StartsWith(RoboboothDeviceNamePrefix, StringComparison.OrdinalIgnoreCase);

    public async Task RemoveStalePairingsAsync()
    {
        await connectionLock.WaitAsync();
        try
        {
            var result = await RemoveSavedRoboboothDevicesAsync(null);
            if (!result.Succeeded)
            {
                logger.LogWarning("Bluetooth startup cleanup failed: {CleanupMessage}", result.Message);
            }
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public async Task RemovePairingForShutdownAsync()
    {
        shutdownCancellation.Cancel();
        logger.LogInformation("Removing saved Robobooth Bluetooth devices before shutdown");
        await connectionLock.WaitAsync();
        try
        {
            var result = await DisconnectAndUnpairTrackedDeviceAsync();
            if (!result.Succeeded)
            {
                logger.LogWarning("Bluetooth shutdown cleanup failed: {CleanupMessage}", result.Message);
            }
            else
            {
                logger.LogInformation("Robobooth Bluetooth shutdown cleanup completed");
            }
        }
        finally
        {
            connectionLock.Release();
        }
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

    private static async Task ProvisionRobotAsync(
        GattCharacteristic provisioningCharacteristic,
        GattCharacteristic wifiStatusCharacteristic,
        WifiCredentials credentials,
        MqttProvisioningSettings mqttSettings,
        CancellationToken cancellationToken)
    {
        var networkNameBytes = Encoding.UTF8.GetBytes(credentials.NetworkName);
        var passwordBytes = Encoding.UTF8.GetBytes(credentials.Password);
        var mqttHostBytes = Encoding.UTF8.GetBytes(mqttSettings.Host);
        var mqttPasswordBytes = Encoding.UTF8.GetBytes(mqttSettings.Password);
        var provisioningBytes = new byte[
            networkNameBytes.Length +
            passwordBytes.Length +
            mqttHostBytes.Length +
            mqttPasswordBytes.Length];

        try
        {
            var writeOffset = 0;
            networkNameBytes.CopyTo(provisioningBytes, writeOffset);
            writeOffset += networkNameBytes.Length;
            passwordBytes.CopyTo(provisioningBytes, writeOffset);
            writeOffset += passwordBytes.Length;
            mqttHostBytes.CopyTo(provisioningBytes, writeOffset);
            writeOffset += mqttHostBytes.Length;
            mqttPasswordBytes.CopyTo(provisioningBytes, writeOffset);

            await WriteProvisioningPacketAsync(
                provisioningCharacteristic,
                [
                    ProvisioningProtocolVersion,
                    ProvisioningStartCommand,
                    checked((byte)networkNameBytes.Length),
                    checked((byte)passwordBytes.Length),
                    checked((byte)mqttHostBytes.Length),
                    checked((byte)mqttPasswordBytes.Length),
                    (byte)(mqttSettings.Port & 0xff),
                    (byte)(mqttSettings.Port >> 8)
                ]);

            for (var offset = 0; offset < provisioningBytes.Length; offset += ProvisioningChunkSize)
            {
                var chunkLength = Math.Min(ProvisioningChunkSize, provisioningBytes.Length - offset);
                var packet = new byte[4 + chunkLength];
                packet[0] = ProvisioningProtocolVersion;
                packet[1] = ProvisioningDataCommand;
                packet[2] = (byte)(offset & 0xff);
                packet[3] = (byte)(offset >> 8);
                provisioningBytes.AsSpan(offset, chunkLength).CopyTo(packet.AsSpan(4));

                try
                {
                    await WriteProvisioningPacketAsync(provisioningCharacteristic, packet);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(packet);
                }
            }

            await WriteProvisioningPacketAsync(
                provisioningCharacteristic,
                [ProvisioningProtocolVersion, ProvisioningCommitCommand]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(networkNameBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(mqttHostBytes);
            CryptographicOperations.ZeroMemory(mqttPasswordBytes);
            CryptographicOperations.ZeroMemory(provisioningBytes);
        }

        var deadline = DateTimeOffset.UtcNow + ProvisioningTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statusResult = await wifiStatusCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (statusResult.Status != GattCommunicationStatus.Success)
            {
                throw new InvalidOperationException(
                    $"The Robobooth Wi-Fi status could not be read ({statusResult.Status}).");
            }

            var wifiStatus = ReadUtf8(statusResult.Value);
            switch (wifiStatus)
            {
                case "mqtt-connected":
                    return;
                case "invalid":
                    throw new InvalidOperationException(
                        "The Robobooth rejected the Wi-Fi configuration payload.");
                case "wifi-failed":
                    throw new InvalidOperationException(
                        "The Robobooth could not join the configured Wi-Fi network. Check the network name and password.");
                case "mqtt-failed":
                    throw new InvalidOperationException(
                        $"The Robobooth joined Wi-Fi but could not reach the embedded MQTT broker at {mqttSettings.Host}:{mqttSettings.Port}.");
            }

            await Task.Delay(WifiStatusPollInterval, cancellationToken);
        }

        throw new TimeoutException(
            "The Robobooth did not establish its Wi-Fi and MQTT connections within 45 seconds.");
    }

    private static async Task WriteProvisioningPacketAsync(
        GattCharacteristic characteristic,
        byte[] packet)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(packet);
        var writeResult = await characteristic.WriteValueWithResultAsync(
            writer.DetachBuffer(),
            GattWriteOption.WriteWithResponse);
        if (writeResult.Status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException(
                $"The Robobooth rejected a Wi-Fi provisioning packet ({writeResult.Status}).");
        }
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

    private static string FormatUnpairingStatus(DeviceUnpairingResultStatus status) => status switch
    {
        DeviceUnpairingResultStatus.AccessDenied => "access was denied",
        DeviceUnpairingResultStatus.OperationAlreadyInProgress => "another unpairing operation is already in progress",
        DeviceUnpairingResultStatus.Failed => "Windows reported that unpairing failed",
        _ => status.ToString()
    };

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shutdownCancellation.Cancel();
        await connectionLock.WaitAsync();
        try
        {
            var result = await DisconnectAndUnpairTrackedDeviceAsync();
            if (!result.Succeeded)
            {
                logger.LogWarning("Bluetooth disposal cleanup failed: {CleanupMessage}", result.Message);
            }
        }
        finally
        {
            connectionLock.Release();
            connectionLock.Dispose();
            shutdownCancellation.Dispose();
        }
    }
}
