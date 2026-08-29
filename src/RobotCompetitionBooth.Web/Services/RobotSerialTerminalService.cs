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
    private readonly object portControlLock = new();
    private readonly Queue<RobotSerialLine> lines = new();
    private RobotSerialConnectionState state = new(false, null, "Waiting for the robot USB serial port.");
    private CancellationTokenSource? activeReadCancellation;
    private TaskCompletionSource<bool> portReleased = CompletedSignal();
    private TaskCompletionSource<bool> resumeRequested = CompletedSignal();
    private int portSuspensionCount;
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

    public async Task<IAsyncDisposable> SuspendPortAsync(
        string portName,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var configured = options.Value;
        var normalizedPortName = portName.Trim().ToUpperInvariant();
        if (!configured.Enabled ||
            string.IsNullOrWhiteSpace(configured.PortName) ||
            !string.Equals(normalizedPortName, configured.PortName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return NoopAsyncDisposable.Instance;
        }

        Task releasedTask;
        lock (portControlLock)
        {
            portSuspensionCount++;
            if (portSuspensionCount == 1)
            {
                resumeRequested = NewSignal();
            }

            activeReadCancellation?.Cancel();
            releasedTask = portReleased.Task;
        }

        var lease = new PortSuspensionLease(this, normalizedPortName);
        try
        {
            await releasedTask.WaitAsync(cancellationToken);
            PublishState(new(false, normalizedPortName, reason));
            logger.LogInformation("Robot USB serial released {PortName}: {Reason}", normalizedPortName, reason);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
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
            await WaitUntilResumedAsync(stoppingToken);
            var readCancellation = TryBeginRead(stoppingToken);
            if (readCancellation is null)
            {
                continue;
            }

            try
            {
                await ReadPortAsync(portName, configured.BaudRate, readCancellation.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
            {
                // The port was deliberately released for an exclusive operation such as firmware flashing.
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
            finally
            {
                CompleteRead(readCancellation);
                readCancellation.Dispose();
            }

            if (IsSuspended())
            {
                continue;
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

    private async Task WaitUntilResumedAsync(CancellationToken cancellationToken)
    {
        Task resumeTask;
        lock (portControlLock)
        {
            resumeTask = resumeRequested.Task;
        }

        await resumeTask.WaitAsync(cancellationToken);
    }

    private CancellationTokenSource? TryBeginRead(CancellationToken stoppingToken)
    {
        lock (portControlLock)
        {
            if (portSuspensionCount > 0)
            {
                return null;
            }

            activeReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            portReleased = NewSignal();
            return activeReadCancellation;
        }
    }

    private void CompleteRead(CancellationTokenSource readCancellation)
    {
        lock (portControlLock)
        {
            if (ReferenceEquals(activeReadCancellation, readCancellation))
            {
                activeReadCancellation = null;
                portReleased.TrySetResult(true);
            }
        }
    }

    private bool IsSuspended()
    {
        lock (portControlLock)
        {
            return portSuspensionCount > 0;
        }
    }

    private void ResumePort(string portName)
    {
        var resumed = false;
        lock (portControlLock)
        {
            if (portSuspensionCount > 0 && --portSuspensionCount == 0)
            {
                resumed = true;
                resumeRequested.TrySetResult(true);
            }
        }

        if (resumed)
        {
            PublishState(new(false, portName, $"Firmware operation finished. Reconnecting USB serial on {portName}."));
            logger.LogInformation("Robot USB serial resuming on {PortName}", portName);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = NewSignal();
        signal.SetResult(true);
        return signal;
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

    private sealed class PortSuspensionLease(
        RobotSerialTerminalService owner,
        string portName) : IAsyncDisposable
    {
        private RobotSerialTerminalService? currentOwner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref currentOwner, null)?.ResumePort(portName);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static NoopAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
