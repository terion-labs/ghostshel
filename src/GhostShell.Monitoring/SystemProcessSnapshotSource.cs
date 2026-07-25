using System.ComponentModel;
using System.Diagnostics;

namespace GhostShell.Monitoring;

internal sealed class SystemProcessSnapshotSource : IProcessSnapshotSource
{
    internal const int MaximumObservedProcesses = 4_096;
    private const int MaximumProcessNameLength = 256;

    public RawProcessCapture Capture(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processes = Process.GetProcesses();
        try
        {
            var ordered = processes
                .Select(process => TryGetProcessId(process, out var processId)
                    ? new ProcessWithId(process, processId)
                    : (ProcessWithId?)null)
                .OfType<ProcessWithId>()
                .OrderBy(item => item.ProcessId)
                .Take(MaximumObservedProcesses)
                .ToArray();
            var observations = new List<RawProcessObservation>(ordered.Length);
            foreach (var item in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observations.Add(Capture(item.Process, item.ProcessId));
            }

            return new RawProcessCapture(
                TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64)),
                processes.Length,
                Array.AsReadOnly(observations.ToArray()),
                processes.Length > MaximumObservedProcesses);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static RawProcessObservation Capture(Process process, int processId)
    {
        return new RawProcessObservation(
            processId,
            SafeProcessName(process, processId),
            TryRead(() => process.WorkingSet64),
            TryRead(() => process.TotalProcessorTime),
            TryRead(() => new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)),
            processId == Environment.ProcessId);
    }

    private static bool TryGetProcessId(Process process, out int processId)
    {
        try
        {
            processId = process.Id;
            return true;
        }
        catch (InvalidOperationException)
        {
            processId = default;
            return false;
        }
    }

    private static string SafeProcessName(Process process, int processId)
    {
        try
        {
            return SanitizeName(process.ProcessName);
        }
        catch (InvalidOperationException)
        {
            return $"Process {processId}";
        }
        catch (Win32Exception)
        {
            return $"Process {processId}";
        }
        catch (NotSupportedException)
        {
            return $"Process {processId}";
        }
    }

    private static string SanitizeName(string value)
    {
        var normalized = new string(value
            .Where(character => !char.IsControl(character))
            .Take(MaximumProcessNameLength)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "Unnamed process" : normalized;
    }

    private static T? TryRead<T>(Func<T> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly record struct ProcessWithId(Process Process, int ProcessId);
}
