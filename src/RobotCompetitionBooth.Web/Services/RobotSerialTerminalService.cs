using System.IO.Ports;
using Microsoft.Extensions.Options;

namespace RobotCompetitionBooth.Web.Services;

public sealed class RobotSerialTerminalService(
    IOptions<RobotSerialOptions> options,
    ILogger<RobotSerialTerminalService> logger) : BackgroundService
{
    private const int MaximumBufferedLines = 2000;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly object stateLock = new();
    private readonly Queue<RobotSerialLine> lines = new();
    private RobotSerialConnectionState state = new(false, null, "Waiting for the robot USB serial port.");
    private long sequence;

    public event EventHandler<RobotSerialLine>? LineReceived;
    public event EventHandler<RobotSerialConnectionState>? StateChanged;

    public RobotSerialConnectionState State
    {
        get
        {
            lock (stateLock)
            {
                return state;
            }
        }
    }

    public IReadOnlyList<RobotSerialLine> GetSnapshot()
    {
        lock (stateLock)
        {
            return lines.ToArray();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var configured = options.Value;
        if (!configured.Enabled)
        {
            PublishState(new(false, null, "USB serial logging is disabled in server configuration."));
            return;
        }

        if (string.IsNullOrWhiteSpace(configured.PortName) || configured.BaudRate <= 0)
        {
            PublishState(new(false, null, "USB serial configuration is invalid."));
            logger.LogError("Robot USB serial configuration is invalid");
            return;
        }

        var portName = configured.PortName.Trim().ToUpperInvariant();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReadPortAsync(portName, configured.BaudRate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidOperationException)
            {
                PublishState(new(
                    false,
                    portName,
                    $"Waiting for {portName}. The port is unavailable or in use."));
                logger.LogDebug(exception, "Robot USB serial port {PortName} is unavailable", portName);
            }

            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ReadPortAsync(string portName, int baudRate, CancellationToken cancellationToken)
    {
        using var port = new SerialPort(portName, baudRate)
        {
            DtrEnable = false,
            RtsEnable = false,
            NewLine = "\n",
            ReadTimeout = 500,
            Encoding = System.Text.Encoding.UTF8
        };
        port.Open();
        port.DiscardInBuffer();

        PublishState(new(true, portName, $"Reading robot USB serial on {portName} at {baudRate:N0} baud."));
        logger.LogInformation("Robot USB serial connected on {PortName} at {BaudRate} baud", portName, baudRate);

        while (!cancellationToken.IsCancellationRequested && port.IsOpen)
        {
            try
            {
                var line = port.ReadLine().TrimEnd('\r');
                PublishLine(line);
            }
            catch (TimeoutException)
            {
                await Task.Yield();
            }
        }
    }

    private void PublishLine(string text)
    {
        var line = new RobotSerialLine(
            Interlocked.Increment(ref sequence),
            DateTimeOffset.Now,
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

    private void PublishState(RobotSerialConnectionState updatedState)
    {
        var changed = false;
        lock (stateLock)
        {
            if (state != updatedState)
            {
                state = updatedState;
                changed = true;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke(this, updatedState);
        }
    }
}
