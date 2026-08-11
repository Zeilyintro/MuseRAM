using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MuseRAM.Core;

public sealed class MemoryStatusService
{
    public bool TryGetSnapshot(out MemorySnapshot snapshot)
    {
        var status = new NativeMethods.MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>()
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            snapshot = default;
            return false;
        }

        snapshot = new MemorySnapshot(status.TotalPhysical, status.AvailablePhysical, status.MemoryLoad)
        {
            CommitLimitBytes = status.TotalPageFile,
            AvailableCommitBytes = status.AvailablePageFile
        };
        return true;
    }
}

public static class WorkingSetTrimResultPolicy
{
    public static TrimResult Create(
        int processId,
        long? workingSetBefore,
        bool setProcessWorkingSetSucceeded,
        int? setProcessWorkingSetErrorCode,
        bool emptyWorkingSetSucceeded,
        int? emptyWorkingSetErrorCode,
        IEnumerable<long?> workingSetAfterSamples)
    {
        var requestSucceeded = setProcessWorkingSetSucceeded || emptyWorkingSetSucceeded;
        var lastValidAfter = workingSetAfterSamples
            .Where(sample => sample.HasValue && sample.Value >= 0)
            .Select(sample => sample!.Value)
            .LastOrDefault(-1);
        var measurementReliable = requestSucceeded &&
            workingSetBefore is >= 0 &&
            lastValidAfter >= 0;
        var before = Math.Max(0, workingSetBefore ?? 0);
        var after = measurementReliable ? lastValidAfter : before;

        var requestMessage = RequestMessage(
            setProcessWorkingSetSucceeded,
            setProcessWorkingSetErrorCode,
            emptyWorkingSetSucceeded,
            emptyWorkingSetErrorCode);
        return new TrimResult(
            processId,
            requestSucceeded,
            before,
            after,
            requestSucceeded ? null : requestMessage)
        {
            SetProcessWorkingSetSucceeded = setProcessWorkingSetSucceeded,
            EmptyWorkingSetSucceeded = emptyWorkingSetSucceeded,
            SetProcessWorkingSetErrorCode = setProcessWorkingSetErrorCode,
            EmptyWorkingSetErrorCode = emptyWorkingSetErrorCode,
            HasReliableWorkingSetMeasurement = measurementReliable,
            Warning = requestSucceeded ? requestMessage : null
        };
    }

    private static string? RequestMessage(
        bool setProcessWorkingSetSucceeded,
        int? setProcessWorkingSetErrorCode,
        bool emptyWorkingSetSucceeded,
        int? emptyWorkingSetErrorCode)
    {
        if (setProcessWorkingSetSucceeded && emptyWorkingSetSucceeded) return null;

        var failures = new List<string>(2);
        if (!setProcessWorkingSetSucceeded)
            failures.Add($"SetProcessWorkingSetSize failed ({setProcessWorkingSetErrorCode?.ToString() ?? "unknown"})");
        if (!emptyWorkingSetSucceeded)
            failures.Add($"EmptyWorkingSet failed ({emptyWorkingSetErrorCode?.ToString() ?? "unknown"})");
        var requestSucceeded = setProcessWorkingSetSucceeded || emptyWorkingSetSucceeded;
        return requestSucceeded
            ? $"Working-set trim partially succeeded: {string.Join("; ", failures)}."
            : $"Working-set trim requests failed: {string.Join("; ", failures)}.";
    }
}

public sealed class WorkingSetTrimmer
{
    private const uint RequiredAccess =
        NativeMethods.ProcessSetQuota |
        NativeMethods.ProcessQueryInformation |
        NativeMethods.ProcessQueryLimitedInformation;

    public async Task<TrimResult> TrimAsync(
        ProcessSnapshot expectedProcess,
        IReadOnlyCollection<int> relatedProcessIds,
        bool enhancedSafety,
        bool allowForegroundProcessTrim,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(
            () => Trim(
                expectedProcess,
                relatedProcessIds,
                enhancedSafety,
                allowForegroundProcessTrim,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static TrimResult Trim(
        ProcessSnapshot expectedProcess,
        IReadOnlyCollection<int> relatedProcessIds,
        bool enhancedSafety,
        bool allowForegroundProcessTrim,
        CancellationToken cancellationToken)
    {
        var totalStarted = Stopwatch.GetTimestamp();
        var openProcessMilliseconds = 0d;
        var identityCheckMilliseconds = 0d;
        var relationshipCheckMilliseconds = 0d;
        var setProcessWorkingSetMilliseconds = 0d;
        var emptyWorkingSetMilliseconds = 0d;
        var measurementMilliseconds = 0d;
        uint? pageFaultCountBefore = null;
        uint? pageFaultCountAfter = null;

        TrimResult Complete(TrimResult result) => result with
        {
            TotalMilliseconds = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds,
            OpenProcessMilliseconds = openProcessMilliseconds,
            IdentityCheckMilliseconds = identityCheckMilliseconds,
            RelationshipCheckMilliseconds = relationshipCheckMilliseconds,
            SetProcessWorkingSetMilliseconds = setProcessWorkingSetMilliseconds,
            EmptyWorkingSetMilliseconds = emptyWorkingSetMilliseconds,
            MeasurementMilliseconds = measurementMilliseconds,
            PageFaultCountBefore = pageFaultCountBefore,
            PageFaultCountAfter = pageFaultCountAfter
        };

        var processId = expectedProcess.ProcessId;
        var stageStarted = Stopwatch.GetTimestamp();
        var handle = NativeMethods.OpenProcess(RequiredAccess, false, processId);
        openProcessMilliseconds = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
        if (handle == IntPtr.Zero)
        {
            return Complete(new TrimResult(processId, false, 0, 0, $"OpenProcess failed ({Marshal.GetLastWin32Error()})."));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stageStarted = Stopwatch.GetTimestamp();
            var identitySafety = CheckIdentity(handle, expectedProcess);
            identityCheckMilliseconds = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            if (!identitySafety.CanTrim)
            {
                return Complete(new TrimResult(
                    processId,
                    false,
                    0,
                    0,
                    identitySafety.SkipReason,
                    Skipped: true));
            }
            stageStarted = Stopwatch.GetTimestamp();
            var beforeSample = TryReadMemorySample(handle);
            measurementMilliseconds += Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            var before = beforeSample?.WorkingSetBytes;
            pageFaultCountBefore = beforeSample?.PageFaultCount;
            if (!before.HasValue)
            {
                return Complete(new TrimResult(
                    processId,
                    false,
                    0,
                    0,
                    "Unable to read the working set before trimming.",
                    Skipped: true));
            }
            stageStarted = Stopwatch.GetTimestamp();
            if (!ProcessRelationshipSnapshot.TryCapture(out var currentParentProcessIds) ||
                !ProcessRelationshipPolicy.TryRefreshSafetyScope(
                    processId,
                    relatedProcessIds,
                    currentParentProcessIds,
                    out var currentSafetyScope))
            {
                relationshipCheckMilliseconds = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
                return Complete(new TrimResult(
                    processId,
                    false,
                    before.Value,
                    before.Value,
                    "无法刷新执行前的进程关系。",
                    Skipped: true)
                {
                    HasReliableWorkingSetMeasurement = true
                });
            }
            var interactionSafety = CheckInteractionSafety(
                currentSafetyScope,
                ForegroundTrimPolicy.IsAllowed(allowForegroundProcessTrim, enhancedSafety));
            relationshipCheckMilliseconds = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            if (!interactionSafety.CanTrim)
            {
                return Complete(new TrimResult(
                    processId,
                    false,
                    before.Value,
                    before.Value,
                    interactionSafety.SkipReason,
                    Skipped: true)
                {
                    HasReliableWorkingSetMeasurement = true
                });
            }

            if (enhancedSafety)
            {
                var safety = CheckSafety(handle, expectedProcess, currentSafetyScope);
                if (!safety.CanTrim)
                {
                    return Complete(new TrimResult(processId, false, before.Value, before.Value, safety.SkipReason, Skipped: true)
                    {
                        HasReliableWorkingSetMeasurement = true
                    });
                }
            }

            stageStarted = Stopwatch.GetTimestamp();
            var setProcessWorkingSetSucceeded = NativeMethods.SetProcessWorkingSetSize(
                handle,
                new IntPtr(-1),
                new IntPtr(-1));
            setProcessWorkingSetMilliseconds = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            int? setProcessWorkingSetErrorCode = setProcessWorkingSetSucceeded
                ? null
                : Marshal.GetLastWin32Error();
            stageStarted = Stopwatch.GetTimestamp();
            var emptyWorkingSetSucceeded = NativeMethods.EmptyWorkingSet(handle);
            emptyWorkingSetMilliseconds = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            int? emptyWorkingSetErrorCode = emptyWorkingSetSucceeded
                ? null
                : Marshal.GetLastWin32Error();
            var afterSamples = new List<ProcessMemorySample?>();
            if (setProcessWorkingSetSucceeded || emptyWorkingSetSucceeded)
            {
                stageStarted = Stopwatch.GetTimestamp();
                afterSamples.Add(TryReadMemorySample(handle));
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    Thread.Sleep(30);
                    cancellationToken.ThrowIfCancellationRequested();
                    afterSamples.Add(TryReadMemorySample(handle));
                }
                measurementMilliseconds += Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
                pageFaultCountAfter = afterSamples
                    .Where(sample => sample.HasValue)
                    .Select(sample => (uint?)sample!.Value.PageFaultCount)
                    .LastOrDefault();
            }
            return Complete(WorkingSetTrimResultPolicy.Create(
                processId,
                before,
                setProcessWorkingSetSucceeded,
                setProcessWorkingSetErrorCode,
                emptyWorkingSetSucceeded,
                emptyWorkingSetErrorCode,
                afterSamples.Select(sample => sample?.WorkingSetBytes)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Complete(new TrimResult(processId, false, 0, 0, exception.Message));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static ProcessMemorySample? TryReadMemorySample(IntPtr handle)
    {
        var size = (uint)Marshal.SizeOf<NativeMethods.ProcessMemoryCounters>();
        if (!NativeMethods.GetProcessMemoryInfo(handle, out var counters, size)) return null;
        try
        {
            return new ProcessMemorySample(
                checked((long)counters.WorkingSetSize.ToUInt64()),
                counters.PageFaultCount);
        }
        catch (OverflowException) { return null; }
    }

    private readonly record struct ProcessMemorySample(long WorkingSetBytes, uint PageFaultCount);

    private static ProcessTrimSafetyCheck CheckSafety(
        IntPtr processHandle,
        ProcessSnapshot expectedProcess,
        IReadOnlyCollection<int> relatedProcessIds)
    {
        long? actualStartTime = NativeMethods.GetProcessTimes(
            processHandle,
            out var creationTime,
            out _,
            out _,
            out _)
            ? creationTime.ToLong()
            : null;
        var foregroundSnapshotSucceeded = TryGetForegroundProcessId(out var foregroundProcessId);
        var visibleWindowSnapshotSucceeded = TryGetVisibleWindowProcessIds(out var visibleWindowProcessIds);
        var relatedIds = relatedProcessIds.ToHashSet();

        return ProcessExecutionSafetyPolicy.EvaluateEnhanced(
            expectedProcess.ProcessId,
            expectedProcess.StartTimeFileTimeUtc,
            actualStartTime,
            relatedIds,
            foregroundSnapshotSucceeded,
            foregroundProcessId,
            visibleWindowSnapshotSucceeded,
            visibleWindowProcessIds);
    }

    private static ProcessTrimSafetyCheck CheckIdentity(
        IntPtr processHandle,
        ProcessSnapshot expectedProcess)
    {
        long? actualStartTime = NativeMethods.GetProcessTimes(
            processHandle,
            out var creationTime,
            out _,
            out _,
            out _)
            ? creationTime.ToLong()
            : null;
        return ProcessIdentitySafetyPolicy.Evaluate(
            expectedProcess.StartTimeFileTimeUtc,
            actualStartTime);
    }

    private static ProcessTrimSafetyCheck CheckInteractionSafety(
        IReadOnlyCollection<int> relatedProcessIds,
        bool allowForegroundProcessTrim)
    {
        var foregroundSnapshotSucceeded = TryGetForegroundProcessId(out var foregroundProcessId);
        return ProcessExecutionSafetyPolicy.EvaluateInteraction(
            foregroundSnapshotSucceeded,
            relatedProcessIds.ToHashSet(),
            foregroundProcessId,
            allowForegroundProcessTrim);
    }

    private static bool TryGetForegroundProcessId(out int? processId)
    {
        processId = null;
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return true;
        if (NativeMethods.GetWindowThreadProcessId(window, out var ownerProcessId) == 0 ||
            ownerProcessId > int.MaxValue)
        {
            return false;
        }

        processId = (int)ownerProcessId;
        return true;
    }

    private static bool TryGetVisibleWindowProcessIds(out IReadOnlySet<int> processIds)
    {
        var result = new HashSet<int>();
        using var virtualDesktopQuery = VirtualDesktopWindowQuery.TryCreate();
        var captureSucceeded = NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window) && !NativeMethods.IsIconic(window))
            {
                return true;
            }
            if (virtualDesktopQuery?.IsOnCurrentDesktopOrUnknown(window) == false) return true;

            if (NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0 ||
                processId > int.MaxValue)
                return false;
            result.Add((int)processId);

            return true;
        }, IntPtr.Zero);
        processIds = result;
        return captureSucceeded;
    }
}
