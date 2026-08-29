using System.Collections.Concurrent;
using RobotCompetitionBooth.Web.Models;

namespace RobotCompetitionBooth.Web.Services;

public sealed class RobotDiagnosticLogService
{
    private const int MaximumLinesPerRobot = 2000;

    private readonly ConcurrentDictionary<string, DeviceLogBuffer> buffers =
        new(StringComparer.Ordinal);

    public event EventHandler<RobotDiagnosticLogEntry>? LogReceived;

    public void Append(string deviceId, RobotDiagnosticLogMessage message)
    {
        var buffer = buffers.GetOrAdd(deviceId, static _ => new());
        RobotDiagnosticLogEntry entry;
        lock (buffer.SyncRoot)
        {
            entry = new(
                ++buffer.HostSequence,
                deviceId,
                message.Sequence,
                message.UptimeMs,
                message.Level,
                message.Source,
                message.Message,
                DateTimeOffset.Now);
            buffer.Lines.Enqueue(entry);
            while (buffer.Lines.Count > MaximumLinesPerRobot)
            {
                buffer.Lines.Dequeue();
            }
        }

        LogReceived?.Invoke(this, entry);
    }

    public IReadOnlyList<RobotDiagnosticLogEntry> GetSnapshot(string deviceId)
    {
        if (!buffers.TryGetValue(deviceId, out var buffer))
        {
            return [];
        }

        lock (buffer.SyncRoot)
        {
            return buffer.Lines.ToArray();
        }
    }

    public int GetLineCount(string deviceId)
    {
        if (!buffers.TryGetValue(deviceId, out var buffer))
        {
            return 0;
        }

        lock (buffer.SyncRoot)
        {
            return buffer.Lines.Count;
        }
    }

    private sealed class DeviceLogBuffer
    {
        public object SyncRoot { get; } = new();
        public Queue<RobotDiagnosticLogEntry> Lines { get; } = new();
        public long HostSequence { get; set; }
    }
}
