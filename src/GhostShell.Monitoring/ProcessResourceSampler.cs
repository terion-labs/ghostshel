using System.Diagnostics;
using GhostShell.Application;

namespace GhostShell.Monitoring;

internal enum ProcessResourceConsumer
{
    Unspecified,
    Statistics,
    ProcessMonitor,
}

internal sealed class ProcessResourceSampler
{
    private static readonly TimeSpan CaptureReuseWindow = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly IProcessSnapshotSource _source;
    private readonly TimeProvider _timeProvider;
    private MonitorPanelResult<ProcessResourceSample>? _latestCapture;
    private long _captureGeneration;
    private long _latestCaptureTimestamp;
    private ProcessResourceConsumer _latestConsumer;
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
        CancellationToken cancellationToken) =>
        await CaptureAsync(
            ProcessResourceConsumer.Unspecified,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<MonitorPanelResult<ProcessResourceSample>> CaptureAsync(
        ProcessResourceConsumer consumer,
        CancellationToken cancellationToken)
    {
        var observedGeneration = Volatile.Read(ref _captureGeneration);
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
            var overlappingCaptureCompleted = _captureGeneration != observedGeneration;
            var otherPanelCapturedRecently =
                consumer != ProcessResourceConsumer.Unspecified
                && _latestConsumer != consumer
                && Stopwatch.GetElapsedTime(_latestCaptureTimestamp) <= CaptureReuseWindow;
            var canReuseLatest = _latestCapture is { }
                && (overlappingCaptureCompleted || otherPanelCapturedRecently);
            if (canReuseLatest)
            {
                return _latestCapture!;
            }

            var captured = await CaptureCoreAsync(cancellationToken).ConfigureAwait(false);
            _latestCapture = captured;
            _latestCaptureTimestamp = Stopwatch.GetTimestamp();
            _latestConsumer = consumer;
            Interlocked.Increment(ref _captureGeneration);
            return captured;
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async ValueTask<MonitorPanelResult<ProcessResourceSample>> CaptureCoreAsync(
        CancellationToken cancellationToken)
    {
        RawProcessCapture raw;
        try
        {
            raw = await _source
                .CaptureAsync(cancellationToken)
                .ConfigureAwait(false);
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
        catch (Exception exception) when (IsRecoverableCaptureFailure(exception))
        {
            return Failure(MonitorPanelErrorCode.CaptureFailed);
        }

        try
        {
            return BuildSample(raw, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception exception) when (IsRecoverableCaptureFailure(exception))
        {
            // External process data is untrusted. Parser and projection failures remain typed
            // monitoring failures instead of escaping through the session-host engine boundary.
            return Failure(MonitorPanelErrorCode.CaptureFailed);
        }
    }

    private MonitorPanelResult<ProcessResourceSample> BuildSample(
        RawProcessCapture raw,
        CancellationToken cancellationToken)
    {
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
        var observedProcesses = 0;

        foreach (var process in raw.Processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double? cpu = null;
            if (process.TotalProcessorTime is { } currentProcessorTime)
            {
                var identity = new ProcessIdentity(process.ProcessId, process.Name);
                currentProcessorTimes[identity] = currentProcessorTime;
                if (elapsed > TimeSpan.Zero
                    && _previousProcessorTimes.TryGetValue(identity, out var previousProcessorTime)
                    && currentProcessorTime >= previousProcessorTime)
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
            observedWorkingSet);
        return MonitorPanelResult<ProcessResourceSample>.Success(
            new ProcessResourceSample(
                statistics,
                Array.AsReadOnly(entries.ToArray()),
                raw.EnumeratedProcessCount,
                observedProcesses,
                raw.IsTruncated));
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

    private static bool IsRecoverableCaptureFailure(Exception exception) =>
        exception is not OutOfMemoryException;

    private static MonitorPanelResult<ProcessResourceSample> Cancelled() =>
        Failure(MonitorPanelErrorCode.Cancelled);

    private static MonitorPanelResult<ProcessResourceSample> Failure(
        MonitorPanelErrorCode code) =>
        MonitorPanelResult<ProcessResourceSample>.Failure(MonitorPanelError.Create(code));

    private readonly record struct ProcessIdentity(
        int ProcessId,
        string Name);
}
