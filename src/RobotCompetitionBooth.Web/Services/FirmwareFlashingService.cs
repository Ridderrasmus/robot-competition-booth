using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;

namespace RobotCompetitionBooth.Web.Services;

public sealed partial class FirmwareFlashingService(
    IOptions<FirmwareFlashingOptions> options,
    IOptions<RobotSerialOptions> serialOptions,
    RobotSerialTerminalService serialTerminal,
    IHostEnvironment environment,
    ILogger<FirmwareFlashingService> logger)
{
    public const string RobotNamePrefix = "RobotBooth-";
    public const int MaximumRobotNameLength = 24;
    private const int MaximumBufferedLines = 2000;
    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(10);

    private readonly object stateLock = new();
    private readonly SemaphoreSlim flashLock = new(1, 1);
    private readonly Queue<FirmwareFlashLine> lines = new();
    private FirmwareFlashState state = new(false, null, "No firmware flash has been started.", null, null, null);
    private CancellationTokenSource? currentFlashCancellation;
    private Process? currentProcess;
    private bool currentFlashWasCancelledByUser;
    private long sequence;

    public event EventHandler<FirmwareFlashLine>? LineReceived;
    public event EventHandler<FirmwareFlashState>? StateChanged;

    public FirmwareFlashState State
    {
        get
        {
            lock (stateLock)
            {
                return state;
            }
        }
    }

    public IReadOnlyList<FirmwareFlashLine> GetSnapshot()
    {
        lock (stateLock)
        {
            return lines.ToArray();
        }
    }

    public async Task<IReadOnlyList<FirmwareSerialPort>> ListPortsAsync()
    {
        var portNames = SerialPort.GetPortNames()
            .Select(portName => portName.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortSortKey)
            .ThenBy(portName => portName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var friendlyNames = await ReadFriendlyPortNamesAsync();
        var configuredPort = serialOptions.Value.PortName?.Trim() ?? string.Empty;
        return portNames
            .Select(portName => new FirmwareSerialPort(
                portName,
                friendlyNames.GetValueOrDefault(portName, portName),
                string.Equals(portName, configuredPort, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public async Task<PlatformIoAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        var configured = options.Value;
        if (!configured.Enabled)
        {
            return new(false, "Firmware flashing is disabled in server configuration.", null, null);
        }

        var projectDirectory = ResolveProjectDirectory(configured.ProjectDirectory);
        if (projectDirectory is null)
        {
            return new(false, "The PlatformIO firmware project could not be found on this server.", null, null);
        }

        if (string.IsNullOrWhiteSpace(configured.PlatformIoExecutable))
        {
            return new(false, "No PlatformIO executable is configured.", null, projectDirectory);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AvailabilityTimeout);
            var result = await RunForResultAsync(
                configured.PlatformIoExecutable.Trim(),
                ["--version"],
                projectDirectory,
                timeout.Token);
            var output = string.Join(' ', new[] { result.StandardOutput, result.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
                .Trim();

            return result.ExitCode == 0
                ? new(true, output.Length > 0 ? output : "PlatformIO CLI is available.", output, projectDirectory)
                : new(false, $"PlatformIO returned exit code {result.ExitCode}: {output}", null, projectDirectory);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "PlatformIO did not respond within 10 seconds.", null, projectDirectory);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(false, $"PlatformIO CLI is unavailable: {CleanMessage(exception)}", null, projectDirectory);
        }
    }

    public async Task<FirmwareFlashResult> FlashAsync(
        string portName,
        FirmwareBuildConfiguration buildConfiguration,
        CancellationToken cancellationToken = default)
    {
        if (!await flashLock.WaitAsync(0, cancellationToken))
        {
            return new(false, "Another firmware flash is already running.");
        }

        try
        {
            return await FlashCoreAsync(portName, buildConfiguration, cancellationToken);
        }
        finally
        {
            flashLock.Release();
        }
    }

    public void CancelCurrentFlash()
    {
        CancellationTokenSource? cancellation;
        Process? process;
        lock (stateLock)
        {
            currentFlashWasCancelledByUser = true;
            cancellation = currentFlashCancellation;
            process = currentProcess;
        }

        cancellation?.Cancel();
        TryKill(process);
    }

    private async Task<FirmwareFlashResult> FlashCoreAsync(
        string portName,
        FirmwareBuildConfiguration buildConfiguration,
        CancellationToken cancellationToken)
    {
        var normalizedPort = portName.Trim().ToUpperInvariant();
        if (!ComPortRegex().IsMatch(normalizedPort))
        {
            return new(false, "Select a valid COM port.");
        }

        var configurationError = ValidateBuildConfiguration(buildConfiguration);
        if (configurationError is not null)
        {
            return new(false, configurationError);
        }

        var robotName = buildConfiguration.RobotName.Trim();
        var pairingPasskey = uint.Parse(buildConfiguration.PairingCode);

        var ports = await ListPortsAsync();
        if (!ports.Any(port => string.Equals(port.PortName, normalizedPort, StringComparison.OrdinalIgnoreCase)))
        {
            return new(false, $"{normalizedPort} is no longer available.");
        }

        var availability = await CheckAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable || availability.ProjectDirectory is null)
        {
            return new(false, availability.Message);
        }

        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.Environment) || configured.TimeoutMinutes is < 1 or > 60)
        {
            return new(false, "Firmware flashing configuration is invalid.");
        }

        ClearOutput();
        var startedAt = DateTimeOffset.Now;
        PublishState(new(true, normalizedPort, $"Preparing to flash {normalizedPort}.", startedAt, null, null));
        PublishLine(false, $"PlatformIO project: {availability.ProjectDirectory}");
        PublishLine(false, $"Environment: {configured.Environment}");
        PublishLine(false, $"Upload port: {normalizedPort}");
        PublishLine(false, $"Robot name: {robotName}");
        PublishLine(false, "A custom BLE connection code will be compiled into this image.");

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(TimeSpan.FromMinutes(configured.TimeoutMinutes));
        lock (stateLock)
        {
            currentFlashCancellation = operationCancellation;
            currentFlashWasCancelledByUser = false;
        }

        try
        {
            await using var serialLease = await serialTerminal.SuspendPortAsync(
                normalizedPort,
                $"USB serial is paused while firmware is flashed to {normalizedPort}.",
                operationCancellation.Token);

            PublishLine(false, "Serial port released. Starting PlatformIO upload...");
            PublishState(new(true, normalizedPort, $"Flashing firmware to {normalizedPort}.", startedAt, null, null));

            var exitCode = await RunFlashProcessAsync(
                configured.PlatformIoExecutable.Trim(),
                availability.ProjectDirectory,
                configured.Environment.Trim(),
                normalizedPort,
                robotName,
                pairingPasskey,
                operationCancellation.Token);

            if (exitCode != 0)
            {
                var failedMessage = $"PlatformIO exited with code {exitCode}. See the flash log for details.";
                PublishState(new(false, normalizedPort, failedMessage, startedAt, DateTimeOffset.Now, false));
                return new(false, failedMessage);
            }

            const string successMessage = "Firmware flashed successfully. USB serial is reconnecting.";
            PublishLine(false, successMessage);
            PublishState(new(false, normalizedPort, successMessage, startedAt, DateTimeOffset.Now, true));
            return new(true, successMessage);
        }
        catch (OperationCanceledException)
        {
            bool cancelledByUser;
            lock (stateLock)
            {
                cancelledByUser = currentFlashWasCancelledByUser;
            }

            var timedOut = !cancelledByUser &&
                !cancellationToken.IsCancellationRequested &&
                operationCancellation.IsCancellationRequested;
            var message = timedOut
                ? $"Firmware flashing exceeded the {configured.TimeoutMinutes}-minute timeout."
                : "Firmware flashing was cancelled.";
            PublishLine(true, message);
            PublishState(new(false, normalizedPort, message, startedAt, DateTimeOffset.Now, false));
            return new(false, message);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var message = $"Firmware flashing failed: {CleanMessage(exception)}";
            PublishLine(true, message);
            PublishState(new(false, normalizedPort, message, startedAt, DateTimeOffset.Now, false));
            logger.LogError(exception, "Firmware flashing failed on {PortName}", normalizedPort);
            return new(false, message);
        }
        finally
        {
            lock (stateLock)
            {
                currentFlashCancellation = null;
                currentProcess = null;
                currentFlashWasCancelledByUser = false;
            }
        }
    }

    private async Task<int> RunFlashProcessAsync(
        string executable,
        string projectDirectory,
        string environmentName,
        string portName,
        string robotName,
        uint pairingPasskey,
        CancellationToken cancellationToken)
    {
        var buildFlags = string.Join(' ',
            "-DARDUINO_USB_MODE=1",
            "-DARDUINO_USB_CDC_ON_BOOT=1",
            $"-DROBOBOOTH_DEVICE_NAME=\\\"{robotName}\\\"",
            $"-DROBOBOOTH_PAIRING_PASSKEY={pairingPasskey.ToString(CultureInfo.InvariantCulture)}");
        using var process = CreateProcess(
            executable,
            projectDirectory,
            ["run", "--project-dir", projectDirectory, "--environment", environmentName, "--target", "upload", "--upload-port", portName],
            new Dictionary<string, string> { ["PLATFORMIO_BUILD_FLAGS"] = buildFlags });
        if (!process.Start())
        {
            throw new InvalidOperationException("PlatformIO could not be started.");
        }

        lock (stateLock)
        {
            currentProcess = process;
        }

        var standardOutput = PumpOutputAsync(process.StandardOutput, false, cancellationToken);
        var standardError = PumpOutputAsync(process.StandardError, true, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(standardOutput, standardError);
            return process.ExitCode;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private async Task PumpOutputAsync(
        StreamReader reader,
        bool isError,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            PublishLine(isError, line);
        }
    }

    private static async Task<ProcessResult> RunForResultAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(executable, workingDirectory, arguments);
        if (!process.Start())
        {
            throw new InvalidOperationException("The process could not be started.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new(process.ExitCode, await standardOutput, await standardError);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static Process CreateProcess(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
            {
                startInfo.Environment[name] = value;
            }
        }

        return new Process { StartInfo = startInfo };
    }

    private async Task<Dictionary<string, string>> ReadFriendlyPortNamesAsync()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var devices = await DeviceInformation.FindAllAsync(SerialDevice.GetDeviceSelector());
            foreach (var device in devices)
            {
                var match = ComPortInNameRegex().Match(device.Name);
                if (match.Success)
                {
                    names[match.Groups[1].Value.ToUpperInvariant()] = device.Name;
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Friendly serial-port names could not be read from Windows");
        }

        return names;
    }

    private string? ResolveProjectDirectory(string configuredDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            candidates.Add(Path.IsPathRooted(configuredDirectory)
                ? configuredDirectory
                : Path.Combine(environment.ContentRootPath, configuredDirectory));
        }

        candidates.Add(Path.Combine(environment.ContentRootPath, "..", "RobotCompetitionBooth.Firmware"));
        candidates.Add(Path.Combine(environment.ContentRootPath, "firmware"));

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "platformio.ini")));
    }

    private void ClearOutput()
    {
        lock (stateLock)
        {
            lines.Clear();
        }
    }

    private void PublishLine(bool isError, string text)
    {
        var line = new FirmwareFlashLine(
            Interlocked.Increment(ref sequence),
            DateTimeOffset.Now,
            isError,
            text);
        lock (stateLock)
        {
            lines.Enqueue(line);
            while (lines.Count > MaximumBufferedLines)
            {
                lines.Dequeue();
            }
        }

        LineReceived?.Invoke(this, line);
    }

    private void PublishState(FirmwareFlashState updatedState)
    {
        lock (stateLock)
        {
            state = updatedState;
        }

        StateChanged?.Invoke(this, updatedState);
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process has already exited or cannot be terminated further.
        }
    }

    private static int PortSortKey(string portName) =>
        int.TryParse(portName.AsSpan(3), out var number) ? number : int.MaxValue;

    private static string CleanMessage(Exception exception) =>
        exception.Message.ReplaceLineEndings(" ").Trim();

    public static string? ValidateBuildConfiguration(FirmwareBuildConfiguration configuration)
    {
        var robotName = configuration.RobotName?.Trim() ?? string.Empty;
        if (!RobotNameRegex().IsMatch(robotName) || robotName.Length > MaximumRobotNameLength)
        {
            return $"Robot name must start with {RobotNamePrefix} and use only letters, numbers, hyphens, or underscores ({MaximumRobotNameLength} characters maximum).";
        }

        if (!PairingCodeRegex().IsMatch(configuration.PairingCode ?? string.Empty))
        {
            return "Connection code must contain between one and six digits.";
        }

        return null;
    }

    [GeneratedRegex("^COM[1-9][0-9]{0,4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComPortRegex();

    [GeneratedRegex(@"\b(COM[1-9][0-9]{0,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComPortInNameRegex();

    [GeneratedRegex("^RobotBooth-[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RobotNameRegex();

    [GeneratedRegex("^[0-9]{1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex PairingCodeRegex();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
