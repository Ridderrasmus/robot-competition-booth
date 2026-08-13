using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RobotCompetitionBooth.Web.Models;

namespace RobotCompetitionBooth.Web.Services;

public sealed class WifiNetworkScanner(ILogger<WifiNetworkScanner> logger)
{
    private const uint ClientVersion = 2;
    private const uint ErrorSuccess = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint ConnectedNetworkFlag = 1;
    private const int InterfaceListHeaderLength = sizeof(uint) * 2;
    private const int AvailableNetworkListHeaderLength = sizeof(uint) * 2;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim scanLock = new(1, 1);

    public async Task<WifiNetworkScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        await scanLock.WaitAsync(cancellationToken);
        try
        {
            return await ScanCoreAsync(cancellationToken);
        }
        catch (DllNotFoundException exception)
        {
            logger.LogError(exception, "The Windows Native Wi-Fi API is unavailable.");
            return new([], "Wi-Fi scanning is only available when the app is running on Windows.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not scan the host computer's Wi-Fi networks.");
            return new([], GetFriendlyErrorMessage(exception));
        }
        finally
        {
            scanLock.Release();
        }
    }

    private async Task<WifiNetworkScanResult> ScanCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfFailed(
            NativeMethods.WlanOpenHandle(
                ClientVersion,
                IntPtr.Zero,
                out _,
                out var clientHandle),
            "open the Windows Wi-Fi service");

        try
        {
            var interfaces = GetInterfaces(clientHandle);
            if (interfaces.Count == 0)
            {
                return new([], "No Windows Wi-Fi adapter was found. Turn on Wi-Fi or connect a Wi-Fi adapter, then scan again.");
            }

            uint? scanError = null;
            var startedScan = false;
            foreach (var wifiInterface in interfaces)
            {
                var interfaceId = wifiInterface.InterfaceId;
                var result = NativeMethods.WlanScan(
                    clientHandle,
                    ref interfaceId,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
                if (result == ErrorSuccess)
                {
                    startedScan = true;
                }
                else
                {
                    scanError ??= result;
                }
            }

            if (startedScan)
            {
                // WlanScan completes asynchronously. A short delay lets the adapter refresh its list;
                // GetAvailableNetworkList still provides the last known list if a radio is slow.
                await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            }

            var discoveredNetworks = new Dictionary<string, WifiNetworkInfo>(StringComparer.Ordinal);
            uint? listError = null;
            foreach (var wifiInterface in interfaces)
            {
                var interfaceId = wifiInterface.InterfaceId;
                var result = NativeMethods.WlanGetAvailableNetworkList(
                    clientHandle,
                    ref interfaceId,
                    0,
                    IntPtr.Zero,
                    out var networkListPointer);
                if (result != ErrorSuccess)
                {
                    listError ??= result;
                    continue;
                }

                try
                {
                    AddNetworks(networkListPointer, discoveredNetworks);
                }
                finally
                {
                    NativeMethods.WlanFreeMemory(networkListPointer);
                }
            }

            var networks = discoveredNetworks.Values
                .OrderByDescending(network => network.IsConnected)
                .ThenByDescending(network => network.SignalQuality)
                .ThenBy(network => network.NetworkName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            var error = listError ?? (networks.Length == 0 ? scanError : null);
            return new(networks, GetScanMessage(error, networks.Length));
        }
        finally
        {
            NativeMethods.WlanCloseHandle(clientHandle, IntPtr.Zero);
        }
    }

    private static List<WifiInterface> GetInterfaces(IntPtr clientHandle)
    {
        ThrowIfFailed(
            NativeMethods.WlanEnumInterfaces(clientHandle, IntPtr.Zero, out var interfaceListPointer),
            "enumerate Windows Wi-Fi adapters");

        try
        {
            var interfaceCount = checked((int)(uint)Marshal.ReadInt32(interfaceListPointer));
            var interfaceSize = Marshal.SizeOf<NativeMethods.WlanInterfaceInfo>();
            var interfaces = new List<WifiInterface>(interfaceCount);
            var itemPointer = IntPtr.Add(interfaceListPointer, InterfaceListHeaderLength);

            for (var index = 0; index < interfaceCount; index++)
            {
                var nativeInterface = Marshal.PtrToStructure<NativeMethods.WlanInterfaceInfo>(itemPointer);
                interfaces.Add(new WifiInterface(nativeInterface.InterfaceGuid));
                itemPointer = IntPtr.Add(itemPointer, interfaceSize);
            }

            return interfaces;
        }
        finally
        {
            NativeMethods.WlanFreeMemory(interfaceListPointer);
        }
    }

    private void AddNetworks(
        IntPtr networkListPointer,
        IDictionary<string, WifiNetworkInfo> discoveredNetworks)
    {
        var networkCount = checked((int)(uint)Marshal.ReadInt32(networkListPointer));
        var networkSize = Marshal.SizeOf<NativeMethods.WlanAvailableNetwork>();
        var itemPointer = IntPtr.Add(networkListPointer, AvailableNetworkListHeaderLength);

        for (var index = 0; index < networkCount; index++)
        {
            var nativeNetwork = Marshal.PtrToStructure<NativeMethods.WlanAvailableNetwork>(itemPointer);
            itemPointer = IntPtr.Add(itemPointer, networkSize);

            if (!TryGetNetworkName(nativeNetwork.Ssid, out var networkName))
            {
                continue;
            }

            var network = new WifiNetworkInfo(
                networkName,
                checked((int)Math.Min(nativeNetwork.SignalQuality, 100)),
                nativeNetwork.SecurityEnabled,
                GetSecurityLabel(nativeNetwork.SecurityEnabled, nativeNetwork.DefaultAuthAlgorithm),
                (nativeNetwork.Flags & ConnectedNetworkFlag) != 0);

            if (!discoveredNetworks.TryGetValue(networkName, out var existing) ||
                network.IsConnected && !existing.IsConnected ||
                network.IsConnected == existing.IsConnected && network.SignalQuality > existing.SignalQuality)
            {
                discoveredNetworks[networkName] = network;
            }
        }
    }

    private static bool TryGetNetworkName(NativeMethods.Dot11Ssid ssid, out string networkName)
    {
        networkName = string.Empty;
        if (ssid.SsidLength is 0 or > 32 || ssid.SsidBytes is null)
        {
            return false;
        }

        try
        {
            networkName = StrictUtf8.GetString(ssid.SsidBytes, 0, checked((int)ssid.SsidLength));
            return networkName.Length > 0 && !networkName.Contains('\0');
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string GetSecurityLabel(bool securityEnabled, NativeMethods.Dot11AuthAlgorithm authentication) =>
        securityEnabled
            ? authentication switch
            {
                NativeMethods.Dot11AuthAlgorithm.WpaPsk => "WPA-Personal",
                NativeMethods.Dot11AuthAlgorithm.RsnaPsk => "WPA2-Personal",
                NativeMethods.Dot11AuthAlgorithm.Wpa3Sae => "WPA3-Personal",
                NativeMethods.Dot11AuthAlgorithm.Wpa => "WPA-Enterprise",
                NativeMethods.Dot11AuthAlgorithm.Rsna => "WPA2-Enterprise",
                NativeMethods.Dot11AuthAlgorithm.Wpa3 => "WPA3-Enterprise",
                _ => "Secured"
            }
            : "Open";

    private static string? GetScanMessage(uint? error, int networkCount)
    {
        if (error is null)
        {
            return networkCount == 0
                ? "No Wi-Fi networks were found. Move closer to an access point and scan again."
                : null;
        }

        if (error == ErrorAccessDenied)
        {
            return "Windows denied access to nearby Wi-Fi networks. Enable Location services and allow desktop apps to access your location, then scan again.";
        }

        return $"Windows could not refresh the Wi-Fi list: {new Win32Exception(checked((int)error.Value)).Message}";
    }

    private static string GetFriendlyErrorMessage(Exception exception)
    {
        if (exception is Win32Exception win32Exception &&
            win32Exception.NativeErrorCode == ErrorAccessDenied)
        {
            return "Windows denied access to nearby Wi-Fi networks. Enable Location services and allow desktop apps to access your location, then scan again.";
        }

        return $"Windows could not scan for Wi-Fi networks: {exception.Message}";
    }

    private static void ThrowIfFailed(uint result, string operation)
    {
        if (result != ErrorSuccess)
        {
            throw new Win32Exception(checked((int)result), $"Could not {operation}.");
        }
    }

    private sealed record WifiInterface(Guid InterfaceId);

    private static class NativeMethods
    {
        private const int WlanMaximumNameLength = 256;
        private const int WlanMaximumPhyTypeNumber = 8;

        internal enum Dot11AuthAlgorithm : uint
        {
            Open = 1,
            SharedKey = 2,
            Wpa = 3,
            WpaPsk = 4,
            WpaNone = 5,
            Rsna = 6,
            RsnaPsk = 7,
            Wpa3 = 8,
            Wpa3Sae = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Dot11Ssid
        {
            internal uint SsidLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            internal byte[] SsidBytes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WlanInterfaceInfo
        {
            internal Guid InterfaceGuid;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WlanMaximumNameLength)]
            internal string InterfaceDescription;

            internal uint State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WlanAvailableNetwork
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WlanMaximumNameLength)]
            internal string ProfileName;

            internal Dot11Ssid Ssid;
            internal uint BssType;
            internal uint NumberOfBssids;

            [MarshalAs(UnmanagedType.Bool)]
            internal bool NetworkConnectable;

            internal uint NotConnectableReason;
            internal uint NumberOfPhyTypes;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = WlanMaximumPhyTypeNumber)]
            internal uint[] PhyTypes;

            [MarshalAs(UnmanagedType.Bool)]
            internal bool MorePhyTypes;

            internal uint SignalQuality;

            [MarshalAs(UnmanagedType.Bool)]
            internal bool SecurityEnabled;

            internal Dot11AuthAlgorithm DefaultAuthAlgorithm;
            internal uint DefaultCipherAlgorithm;
            internal uint Flags;
            internal uint Reserved;
        }

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanOpenHandle(
            uint clientVersion,
            IntPtr reserved,
            out uint negotiatedVersion,
            out IntPtr clientHandle);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanEnumInterfaces(
            IntPtr clientHandle,
            IntPtr reserved,
            out IntPtr interfaceList);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanScan(
            IntPtr clientHandle,
            ref Guid interfaceGuid,
            IntPtr ssid,
            IntPtr informationElementData,
            IntPtr reserved);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanGetAvailableNetworkList(
            IntPtr clientHandle,
            ref Guid interfaceGuid,
            uint flags,
            IntPtr reserved,
            out IntPtr availableNetworkList);

        [DllImport("wlanapi.dll")]
        internal static extern void WlanFreeMemory(IntPtr memory);
    }
}
