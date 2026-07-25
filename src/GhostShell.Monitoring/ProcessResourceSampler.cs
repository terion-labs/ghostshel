using System.ComponentModel;
using GhostShell.Application;

namespace GhostShell.Monitoring;

internal sealed class ProcessResourceSampler
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly IProcessSnapshotSource _source;
    private readonly TimeProvider _timeProvider;
    private Dictionary<ProcessIdentity, TimeSpan> _previousProcessorTimes = [];
    private long? _previousTimestamp;

    public ProcessResourceSampler(
        IProcessSnapshotSource source,
        TimeProvider timeProvider)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<MonitorPanelResult<ProcessResourceSample>> CaptureAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        try
        {
            RawProcessCapture raw;
            try
            {
                raw = _source.Capture(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled();
            }
            catch (UnauthorizedAccessException)
            {
                return Failure(MonitorPanelErrorCode.AccessDenied);
            }
            catch (PlatformNotSupportedException)
            {
                return Failure(MonitorPanelErrorCode.Unavailable);
            }
            catch (Exception exception) when (IsCaptureFailure(exception))
            {
                return Failure(MonitorPanelErrorCode.CaptureFailed);
            }

            var timestamp = _timeProvider.GetTimestamp();
            var elapsed = _previousTimestamp is { } previousTimestamp
                ? _timeProvider.GetElapsedTime(previousTimestamp, timestamp)
                : TimeSpan.Zero;
            var processorCount = Math.Max(1, Environment.ProcessorCount);
            var currentProcessorTimes = new Dictionary<ProcessIdentity, TimeSpan>();
            var entries = new List<ProcessMonitorEntry>(raw.Processes.Count);
            double observedCpu = 0;
            var hasObservedCpu = false;
            long observedWorkingSet = 0;
            double? ghostShellCpu = null;
            long ghostShellWorkingSet = 0;
            var observedProcesses = 0;

            foreach (var process in raw.Processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double? cpu = null;
                if (process.TotalProcessorTime is { } currentProcessorTime
                    && process.StartedAtUtc is { } startedAtUtc)
                {
                    var identity = new ProcessIdentity(process.ProcessId, startedAtUtc);
                    currentProcessorTimes[identity] = currentProcessorTime;
                    if (elapsed > TimeSpan.Zero
                        && _previousProcessorTimes.TryGetValue(identity, out var previousProcessorTime))
                    {
                        cpu = CpuPercent(
                            currentProcessorTime - previousProcessorTime,
                            elapsed,
                            processorCount);
                        observedCpu += cpu.Value;
                        hasObservedCpu = true;
                    }
                }

                if (process.WorkingSetBytes is { } workingSet)
                {
                    observedWorkingSet = SaturatingAdd(observedWorkingSet, Math.Max(0, workingSet));
                }

                if (process.TotalProcessorTime is not null || process.WorkingSetBytes is not null)
                {
                    observedProcesses++;
                }

                if (process.IsGhostShell)
                {
                    ghostShellCpu = cpu;
                    ghostShellWorkingSet = Math.Max(0, process.WorkingSetBytes ?? 0);
                }

                entries.Add(new ProcessMonitorEntry(
                    process.ProcessId,
                    process.Name,
                    cpu,
                    process.WorkingSetBytes is { } bytes ? Math.Max(0, bytes) : null,
                    process.TotalProcessorTime,
                    process.StartedAtUtc,
                    process.IsGhostShell));
            }

            cancellationToken.ThrowIfCancellationRequested();
            _previousTimestamp = timestamp;
            _previousProcessorTimes = currentProcessorTimes;
            var capturedAt = _timeProvider.GetUtcNow();
            var statistics = new SystemStatisticsSnapshot(
                capturedAt,
                raw.HostUptime,
                processorCount,
                raw.EnumeratedProcessCount,
                observedProcesses,
                hasObservedCpu ? Math.Clamp(observedCpu, 0, 100) : null,
                observedWorkingSet,
                ghostShellCpu,
                ghostShellWorkingSet);
            return MonitorPanelResult<ProcessResourceSample>.Success(
                new ProcessResourceSample(
                    statistics,
                    Array.AsReadOnly(entries.ToArray()),
                    raw.EnumeratedProcessCount,
                    observedProcesses,
                    raw.IsTruncated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private static double CpuPercent(
        TimeSpan processorDelta,
        TimeSpan elapsed,
        int processorCount)
    {
        if (processorDelta <= TimeSpan.Zero || elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        return Math.Clamp(
            processorDelta.TotalMilliseconds / elapsed.TotalMilliseconds / processorCount * 100,
            0,
            100);
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static bool IsCaptureFailure(Exception exception) =>
        exception is InvalidOperationException
            or IOException
            or Win32Exception;

    private static MonitorPanelResult<ProcessResourceSample> Cancelled() =>
        Failure(MonitorPanelErrorCode.Cancelled);

    private static MonitorPanelResult<ProcessResourceSample> Failure(
        MonitorPanelErrorCode code) =>
        MonitorPanelResult<ProcessResourceSample>.Failure(MonitorPanelError.Create(code));

    private readonly record struct ProcessIdentity(
        int ProcessId,
        DateTimeOffset StartedAtUtc);
}
