using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MuseRAM.Core;

public sealed class ProcessSampler
{
    private readonly Dictionary<int, CpuSample> _cpuSamples = new();
    private readonly Dictionary<int, IoSample> _ioSamples = new();
    private readonly Dictionary<int, DateTimeOffset> _lastForegroundTimes = new();
    private readonly Dictionary<int, ProcessIdentity> _processIdentities = new();
    public ProcessCaptureDiagnostics LastCaptureDiagnostics { get; private set; }

    public IReadOnlyList<ProcessSnapshot> Capture(
        IReadOnlyDictionary<int, DateTimeOffset>? lastTrimTimes = null,
        IReadOnlyDictionary<int, long>? lastTrimProcessStartTimes = null)
    {
        var captureStarted = Stopwatch.GetTimestamp();
        var sampledAt = DateTimeOffset.UtcNow;
        var sampledTimestamp = Stopwatch.GetTimestamp();
        var foregroundId = GetForegroundProcessId();
        var relationshipStarted = Stopwatch.GetTimestamp();
        var relationshipSnapshotSucceeded = ProcessRelationshipSnapshot.TryCapture(out var capturedParentIds);
        var parentIds = ProcessRelationshipSnapshot.RequireReliable(
            relationshipSnapshotSucceeded,
            capturedParentIds);
        var relationshipDuration = Stopwatch.GetElapsedTime(relationshipStarted);
        var windowStarted = Stopwatch.GetTimestamp();
        var windowStates = CaptureWindowStates();
        var windowDuration = Stopwatch.GetElapsedTime(windowStarted);
        var seenIds = new HashSet<int>();
        var snapshots = new List<ProcessSnapshot>();
        var processLoopStarted = Stopwatch.GetTimestamp();
        var pathDuration = TimeSpan.Zero;
        var cpuDuration = TimeSpan.Zero;
        var ioDuration = TimeSpan.Zero;
        var slowestPathDuration = TimeSpan.Zero;
        var slowestPathProcessId = 0;
        var mainModuleFallbackCount = 0;
        var pathFailureCount = 0;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }
                    var name = process.ProcessName;
                    var pathStarted = Stopwatch.GetTimestamp();
                    var path = TryGetPath(process, out var usedMainModuleFallback);
                    var currentPathDuration = Stopwatch.GetElapsedTime(pathStarted);
                    pathDuration += currentPathDuration;
                    if (currentPathDuration > slowestPathDuration)
                    {
                        slowestPathDuration = currentPathDuration;
                        slowestPathProcessId = process.Id;
                    }
                    if (usedMainModuleFallback) mainModuleFallbackCount++;
                    if (string.IsNullOrWhiteSpace(path)) pathFailureCount++;
                    if (SystemProcessPolicy.IsAlwaysExcluded(name, path))
                    {
                        continue;
                    }
                    seenIds.Add(process.Id);
                    var startTimeFileTimeUtc = TryGetStartTimeFileTimeUtc(process);
                    ObserveProcessIdentity(
                        process.Id,
                        startTimeFileTimeUtc,
                        name,
                        path);

                    var cpuStarted = Stopwatch.GetTimestamp();
                    var cpu = SampleCpu(process, sampledTimestamp);
                    cpuDuration += Stopwatch.GetElapsedTime(cpuStarted);
                    var ioStarted = Stopwatch.GetTimestamp();
                    var io = SampleIo(process.Id, sampledTimestamp);
                    ioDuration += Stopwatch.GetElapsedTime(ioStarted);
                    var reliable = cpu.IsReliable && io.IsReliable;
                    var isForeground = foregroundId == process.Id;
                    var windowState = windowStates.GetValueOrDefault(process.Id);
                    if (isForeground ||
                        (windowState.HasVisibleWindow && !_lastForegroundTimes.ContainsKey(process.Id)))
                    {
                        _lastForegroundTimes[process.Id] = sampledAt;
                    }
                    var lastForegroundAt = _lastForegroundTimes.TryGetValue(process.Id, out var trackedForegroundAt)
                        ? trackedForegroundAt
                        : (DateTimeOffset?)null;
                    var workingSet = SafeValue(() => process.WorkingSet64);
                    var wasRecentlyTrimmed = ProcessTrimHistoryPolicy.IsRecentlyTrimmed(
                        process.Id,
                        startTimeFileTimeUtc,
                        lastTrimTimes,
                        lastTrimProcessStartTimes,
                        sampledAt);
                    var idleScore = ProcessColdnessPolicy.Calculate(
                        workingSet,
                        cpu.Value,
                        io.Value,
                        isForeground,
                        windowState.HasVisibleWindow,
                        wasRecentlyTrimmed);

                    snapshots.Add(new ProcessSnapshot(
                        process.Id,
                        name,
                        path,
                        parentIds.GetValueOrDefault(process.Id),
                        workingSet,
                        cpu.Value,
                        io.Value,
                        isForeground,
                        windowState.HasVisibleWindow,
                        reliable,
                        idleScore,
                        startTimeFileTimeUtc,
                        TryGetMainWindowTitle(process),
                        windowState.HasMinimizedWindow,
                        lastForegroundAt)
                    {
                        IoReadBytesPerSecond = io.ReadBytesPerSecond,
                        IoWriteBytesPerSecond = io.WriteBytesPerSecond,
                        IoSampleIntervalSeconds = io.SampleIntervalSeconds,
                        IoReadTransferCount = io.ReadTransferCount,
                        IoWriteTransferCount = io.WriteTransferCount,
                        IoReadDeltaBytes = io.ReadDeltaBytes,
                        IoWriteDeltaBytes = io.WriteDeltaBytes
                    });
                }
                catch
                {
                    // Processes can exit or become inaccessible while being sampled.
                }
            }
        }

        var processLoopDuration = Stopwatch.GetElapsedTime(processLoopStarted);
        RemoveStaleSamples(seenIds);
        var totalDuration = Stopwatch.GetElapsedTime(captureStarted);
        var measuredDuration = relationshipDuration + windowDuration + pathDuration + cpuDuration + ioDuration;
        LastCaptureDiagnostics = new ProcessCaptureDiagnostics(
            totalDuration.TotalMilliseconds,
            relationshipDuration.TotalMilliseconds,
            windowDuration.TotalMilliseconds,
            pathDuration.TotalMilliseconds,
            slowestPathDuration.TotalMilliseconds,
            slowestPathProcessId,
            mainModuleFallbackCount,
            pathFailureCount,
            cpuDuration.TotalMilliseconds,
            ioDuration.TotalMilliseconds,
            processLoopDuration.TotalMilliseconds,
            Math.Max(0, (totalDuration - measuredDuration).TotalMilliseconds));
        return snapshots.OrderByDescending(snapshot => snapshot.WorkingSetBytes).ToArray();
    }

    private SampleValue SampleCpu(Process process, long sampledTimestamp) =>
        SampleCpu(process.Id, sampledTimestamp, () => process.TotalProcessorTime);

    internal SampleValue SampleCpu(
        int processId,
        long sampledTimestamp,
        Func<TimeSpan> readTotalProcessorTime)
    {
        if (!TryGetValue(readTotalProcessorTime, out var total))
        {
            return new SampleValue(0, false);
        }

        if (!_cpuSamples.TryGetValue(processId, out var previous))
        {
            _cpuSamples[processId] = new CpuSample(total, sampledTimestamp);
            return new SampleValue(0, false);
        }

        var elapsed = Stopwatch.GetElapsedTime(previous.SampledTimestamp, sampledTimestamp);
        _cpuSamples[processId] = new CpuSample(total, sampledTimestamp);
        if (elapsed <= TimeSpan.FromMilliseconds(100) || total < previous.TotalProcessorTime)
        {
            return new SampleValue(0, false);
        }

        var value = (total - previous.TotalProcessorTime).TotalMilliseconds /
            (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
        return new SampleValue(Math.Clamp(value, 0, 100), true);
    }

    internal void ObserveProcessIdentity(
        int processId,
        long? startTimeFileTimeUtc,
        string name,
        string? executablePath)
    {
        var current = new ProcessIdentity(startTimeFileTimeUtc, name, executablePath);
        if (_processIdentities.TryGetValue(processId, out var previous) &&
            !ProcessIdentity.IsSame(previous, current))
        {
            _cpuSamples.Remove(processId);
            _ioSamples.Remove(processId);
            _lastForegroundTimes.Remove(processId);
        }
        _processIdentities[processId] = current;
    }

    private IoActivitySample SampleIo(int processId, long sampledTimestamp)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return IoActivitySample.Unreliable;
        }

        try
        {
            if (!NativeMethods.GetProcessIoCounters(handle, out var counters))
            {
                return IoActivitySample.Unreliable;
            }
            return SampleIo(
                processId,
                sampledTimestamp,
                counters.ReadTransferCount,
                counters.WriteTransferCount);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    internal IoActivitySample SampleIo(
        int processId,
        long sampledTimestamp,
        ulong readTransferCount,
        ulong writeTransferCount)
    {
        if (!_ioSamples.TryGetValue(processId, out var previous))
        {
            _ioSamples[processId] = new IoSample(
                readTransferCount,
                writeTransferCount,
                sampledTimestamp);
            return IoActivitySample.UnreliableWithCounters(readTransferCount, writeTransferCount);
        }

        var elapsed = Stopwatch.GetElapsedTime(previous.SampledTimestamp, sampledTimestamp);
        _ioSamples[processId] = new IoSample(
            readTransferCount,
            writeTransferCount,
            sampledTimestamp);
        if (elapsed <= TimeSpan.FromMilliseconds(100) ||
            readTransferCount < previous.ReadTransferCount ||
            writeTransferCount < previous.WriteTransferCount)
        {
            return IoActivitySample.Unreliable;
        }

        var seconds = elapsed.TotalSeconds;
        var readDelta = readTransferCount - previous.ReadTransferCount;
        var writeDelta = writeTransferCount - previous.WriteTransferCount;
        var readRate = readDelta / seconds;
        var writeRate = writeDelta / seconds;
        return new IoActivitySample(
            readRate + writeRate,
            readRate,
            writeRate,
            seconds,
            readTransferCount,
            writeTransferCount,
            readDelta,
            writeDelta,
            true);
    }

    private static int? GetForegroundProcessId()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero || NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0)
        {
            return null;
        }

        return processId <= int.MaxValue ? (int)processId : null;
    }

    private static IReadOnlyDictionary<int, ProcessWindowState> CaptureWindowStates()
    {
        var result = new Dictionary<int, ProcessWindowState>();
        using var virtualDesktopQuery = VirtualDesktopWindowQuery.TryCreate();
        var captureSucceeded = NativeMethods.EnumWindows((window, _parameter) =>
        {
            var isVisible = NativeMethods.IsWindowVisible(window);
            if (!isVisible) return true;
            var isOnCurrentVirtualDesktop =
                virtualDesktopQuery?.IsOnCurrentDesktopOrUnknown(window) ?? true;
            var extendedStyle = NativeMethods.GetWindowLong(window, NativeMethods.GwlExStyle);
            var isToolWindow = (extendedStyle & NativeMethods.WsExToolWindow) != 0;
            var isFullyTransparent = false;
            if ((extendedStyle & NativeMethods.WsExLayered) != 0 &&
                NativeMethods.GetLayeredWindowAttributes(
                    window,
                    out _,
                    out var alpha,
                    out var layeredFlags))
            {
                isFullyTransparent = (layeredFlags & NativeMethods.LwaAlpha) != 0 && alpha == 0;
            }
            var isCloaked = NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DwmwaCloaked,
                out var cloaked,
                sizeof(int)) == 0 && cloaked != 0;
            var current = ProcessWindowPolicy.Classify(
                isVisible,
                NativeMethods.IsIconic(window),
                isCloaked,
                isToolWindow,
                isFullyTransparent,
                isOnCurrentVirtualDesktop);
            if (!current.HasVisibleWindow && !current.HasMinimizedWindow) return true;
            if (NativeMethods.GetWindowThreadProcessId(window, out var ownerProcessId) == 0 ||
                ownerProcessId > int.MaxValue)
            {
                return false;
            }

            var processId = (int)ownerProcessId;
            result[processId] = ProcessWindowPolicy.Merge(
                result.GetValueOrDefault(processId),
                current);
            return true;
        }, IntPtr.Zero);
        return ProcessWindowPolicy.RequireReliableCapture(captureSucceeded, result);
    }

    private static string? TryGetPath(Process process, out bool usedMainModuleFallback)
    {
        var fallbackUsed = false;
        try
        {
            var path = ResolveProcessPath(process.Id, () =>
            {
                fallbackUsed = true;
                return process.MainModule?.FileName;
            });
            usedMainModuleFallback = fallbackUsed;
            return path;
        }
        catch
        {
            usedMainModuleFallback = fallbackUsed;
            return null;
        }
    }

    internal static string? ResolveProcessPath(int processId, Func<string?> primaryPathReader)
    {
        ArgumentNullException.ThrowIfNull(primaryPathReader);
        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var path = new StringBuilder(32_768);
                var size = path.Capacity;
                if (NativeMethods.QueryFullProcessImageName(handle, 0, path, ref size) && path.Length > 0)
                    return path.ToString();
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        try { return primaryPathReader(); }
        catch { return null; }
    }

    private static long? TryGetStartTimeFileTimeUtc(Process process)
    {
        try { return process.StartTime.ToUniversalTime().ToFileTimeUtc(); }
        catch { return null; }
    }

    private static string? TryGetMainWindowTitle(Process process)
    {
        try
        {
            var title = process.MainWindowTitle;
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch
        {
            return null;
        }
    }

    private void RemoveStaleSamples(IReadOnlySet<int> seenIds)
    {
        foreach (var id in _cpuSamples.Keys.Where(id => !seenIds.Contains(id)).ToArray()) _cpuSamples.Remove(id);
        foreach (var id in _ioSamples.Keys.Where(id => !seenIds.Contains(id)).ToArray()) _ioSamples.Remove(id);
        foreach (var id in _lastForegroundTimes.Keys.Where(id => !seenIds.Contains(id)).ToArray()) _lastForegroundTimes.Remove(id);
        foreach (var id in _processIdentities.Keys.Where(id => !seenIds.Contains(id)).ToArray()) _processIdentities.Remove(id);
    }

    private static T SafeValue<T>(Func<T> getValue)
    {
        try { return getValue(); }
        catch { return default!; }
    }

    private static bool TryGetValue<T>(Func<T> getValue, out T value)
    {
        try
        {
            value = getValue();
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }

    private readonly record struct CpuSample(TimeSpan TotalProcessorTime, long SampledTimestamp);
    private readonly record struct IoSample(
        ulong ReadTransferCount,
        ulong WriteTransferCount,
        long SampledTimestamp);
    private readonly record struct ProcessIdentity(long? StartTimeFileTimeUtc, string Name, string? ExecutablePath)
    {
        public static bool IsSame(ProcessIdentity left, ProcessIdentity right)
        {
            if (left.StartTimeFileTimeUtc.HasValue || right.StartTimeFileTimeUtc.HasValue)
                return left.StartTimeFileTimeUtc == right.StartTimeFileTimeUtc;
            return string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }
    internal readonly record struct SampleValue(double Value, bool IsReliable);
    internal readonly record struct IoActivitySample(
        double Value,
        double ReadBytesPerSecond,
        double WriteBytesPerSecond,
        double SampleIntervalSeconds,
        ulong ReadTransferCount,
        ulong WriteTransferCount,
        ulong ReadDeltaBytes,
        ulong WriteDeltaBytes,
        bool IsReliable)
    {
        internal static IoActivitySample Unreliable => new(0, 0, 0, 0, 0, 0, 0, 0, false);

        internal static IoActivitySample UnreliableWithCounters(
            ulong readTransferCount,
            ulong writeTransferCount) =>
            new(0, 0, 0, 0, readTransferCount, writeTransferCount, 0, 0, false);
    }
}

public readonly record struct ProcessCaptureDiagnostics(
    double TotalMilliseconds,
    double RelationshipSnapshotMilliseconds,
    double WindowEnumerationMilliseconds,
    double PathReadMilliseconds,
    double SlowestPathReadMilliseconds,
    int SlowestPathProcessId,
    int MainModuleFallbackCount,
    int PathFailureCount,
    double CpuReadMilliseconds,
    double IoReadMilliseconds,
    double ProcessLoopMilliseconds,
    double OtherMilliseconds);

public static class ProcessTrimHistoryPolicy
{
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromMinutes(10);

    internal static bool IsRecentlyTrimmed(
        int processId,
        long? currentStartTimeFileTimeUtc,
        IReadOnlyDictionary<int, DateTimeOffset>? lastTrimTimes,
        IReadOnlyDictionary<int, long>? lastTrimProcessStartTimes,
        DateTimeOffset now) =>
        currentStartTimeFileTimeUtc.HasValue &&
        lastTrimTimes?.TryGetValue(processId, out var lastTrimTime) == true &&
        lastTrimProcessStartTimes?.TryGetValue(processId, out var lastTrimStartTime) == true &&
        currentStartTimeFileTimeUtc.Value == lastTrimStartTime &&
        now - lastTrimTime < TimeSpan.FromMinutes(2);

    internal static bool IsCoolingDown(
        ProcessSnapshot process,
        IReadOnlyDictionary<int, DateTimeOffset> lastTrimTimes,
        IReadOnlyDictionary<int, long>? lastTrimProcessStartTimes,
        DateTimeOffset now,
        TimeSpan cooldown)
    {
        if (!lastTrimTimes.TryGetValue(process.ProcessId, out var lastTrimTime) ||
            now - lastTrimTime >= cooldown)
        {
            return false;
        }

        if (lastTrimProcessStartTimes is null) return true;
        return process.StartTimeFileTimeUtc.HasValue &&
            lastTrimProcessStartTimes.TryGetValue(process.ProcessId, out var lastTrimStartTime) &&
            process.StartTimeFileTimeUtc.Value == lastTrimStartTime;
    }

    public static bool ShouldDiscard(
        DateTimeOffset trimmedAt,
        long recordedStartTimeFileTimeUtc,
        bool currentProcessObserved,
        long? currentStartTimeFileTimeUtc,
        DateTimeOffset now) =>
        now - trimmedAt >= RetentionWindow ||
        (currentProcessObserved &&
         currentStartTimeFileTimeUtc.HasValue &&
         currentStartTimeFileTimeUtc.Value != recordedStartTimeFileTimeUtc);
}

internal static class ProcessRelationshipSnapshot
{
    internal static IReadOnlyDictionary<int, int?> RequireReliable(
        bool captureSucceeded,
        IReadOnlyDictionary<int, int?> parentProcessIds)
    {
        if (!captureSucceeded)
            throw new InvalidOperationException("Unable to capture a complete process relationship snapshot.");
        return parentProcessIds;
    }

    internal static bool TryCapture(out IReadOnlyDictionary<int, int?> parentProcessIds)
    {
        var result = new Dictionary<int, int?>();
        parentProcessIds = result;
        var handle = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;

        try
        {
            var entry = new NativeMethods.ProcessEntry
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry>(),
                ExeFile = string.Empty
            };
            if (!NativeMethods.Process32First(handle, ref entry)) return false;
            do
            {
                if (entry.ProcessId <= int.MaxValue)
                {
                    result[(int)entry.ProcessId] = entry.ParentProcessId <= int.MaxValue
                        ? (int)entry.ParentProcessId
                        : null;
                }
            } while (NativeMethods.Process32Next(handle, ref entry));

            return Marshal.GetLastWin32Error() == NativeMethods.ErrorNoMoreFiles;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}

public readonly record struct ProcessWindowState(bool HasVisibleWindow, bool HasMinimizedWindow);

public static class ProcessWindowPolicy
{
    internal static IReadOnlyDictionary<int, ProcessWindowState> RequireReliableCapture(
        bool captureSucceeded,
        IReadOnlyDictionary<int, ProcessWindowState> windowStates)
    {
        if (!captureSucceeded)
            throw new InvalidOperationException("Unable to capture a complete top-level window snapshot.");
        return windowStates;
    }

    public static ProcessWindowState Classify(
        bool isVisible,
        bool isMinimized,
        bool isCloaked = false,
        bool isToolWindow = false,
        bool isFullyTransparent = false,
        bool isOnCurrentVirtualDesktop = true)
    {
        if (!isVisible || !isOnCurrentVirtualDesktop || isCloaked || isToolWindow || isFullyTransparent)
            return default;
        return new(!isMinimized, isMinimized);
    }

    public static ProcessWindowState Merge(ProcessWindowState current, ProcessWindowState next) =>
        new(
            current.HasVisibleWindow || next.HasVisibleWindow,
            current.HasMinimizedWindow || next.HasMinimizedWindow);
}

public static class ProcessColdnessPolicy
{
    public static double Calculate(
        long workingSet,
        double cpu,
        double io,
        bool isForeground,
        bool hasVisibleWindow,
        bool wasRecentlyTrimmed)
    {
        var score = ProcessIdleConfidencePolicy.CalculateRaw(cpu, io, isForeground, hasVisibleWindow);

        if (workingSet >= 512L.MiB()) score += 20;
        else if (workingSet >= 256L.MiB()) score += 10;
        if (wasRecentlyTrimmed) score -= 20;

        return Math.Clamp(score, 0, 100);
    }
}

public static class ProcessIdleConfidencePolicy
{
    private const double MaximumPositiveRawScore = 87;

    public static double Calculate(
        double cpu,
        double io,
        bool isForeground,
        bool hasVisibleWindow) => Math.Clamp(
            CalculateRaw(cpu, io, isForeground, hasVisibleWindow) / MaximumPositiveRawScore * 100d,
            0,
            100);

    internal static double CalculateRaw(
        double cpu,
        double io,
        bool isForeground,
        bool hasVisibleWindow)
    {
        var score = 0d;
        if (!isForeground) score += 35;
        if (!hasVisibleWindow) score += 15;

        if (cpu <= 0.5) score += 25;
        else if (cpu <= 2) score += 15;
        else if (cpu > 8) score -= 20;

        if (io <= 32d * 1024) score += 12;
        else if (io <= 256d * 1024) score += 6;
        else if (io >= 2d * 1024 * 1024) score -= 18;

        return score;
    }
}

public static class ExperimentalIdleScorePolicy
{
    public static double Calculate(ProcessFamilySnapshot family, TimeSpan idleFor)
    {
        var reliable = family.Processes
            .Where(process => process.HasReliableActivitySample)
            .ToArray();
        if (reliable.Length == 0) return 0;

        var score = 0d;
        if (!family.HasForegroundProcess) score += 10;
        if (!family.HasVisibleWindow) score += 5;

        var maximumCpu = reliable.Max(process => Math.Max(0, process.CpuPercent));
        score += maximumCpu switch
        {
            <= 0.5 => 25,
            <= 2 => 15,
            <= 8 => 5,
            _ => -20
        };

        var maximumIo = reliable.Max(process => Math.Max(0, process.IoBytesPerSecond));
        score += maximumIo switch
        {
            <= 32d * 1024 => 20,
            <= 256d * 1024 => 12,
            < 2d * 1024 * 1024 => 4,
            _ => -15
        };

        score += idleFor.TotalMinutes switch
        {
            >= 30 => 25,
            >= 15 => 18,
            >= 5 => 10,
            >= 1 => 5,
            _ => 0
        };

        score += family.WorkingSetBytes switch
        {
            >= 512L * 1024 * 1024 => 15,
            >= 256L * 1024 * 1024 => 8,
            _ => 0
        };

        return Math.Clamp(score, 0, 100);
    }
}

public static class LocalIdleScoreShadowPolicy
{
    public static double Calculate(ProcessFamilySnapshot family, TimeSpan idleFor)
    {
        var reliable = family.Processes
            .Where(process => process.HasReliableActivitySample)
            .ToArray();
        if (reliable.Length == 0) return 0;

        var totalWeight = reliable.Sum(process => Math.Max(1, process.WorkingSetBytes));
        if (totalWeight <= 0) return 0;

        var score = reliable.Sum(process =>
        {
            var formalWorkingSetBonus = process.WorkingSetBytes >= 512L.MiB()
                ? 20d
                : process.WorkingSetBytes >= 256L.MiB()
                    ? 10d
                    : 0d;
            var localWorkingSetBonus = process.WorkingSetBytes >= 512L.MiB()
                ? 17.5d
                : process.WorkingSetBytes >= 256L.MiB()
                    ? 8.5d
                    : 0d;
            var formalCpuBonus = FormalCpuBonus(process.CpuPercent);
            var formalIoBonus = FormalIoBonus(process.IoBytesPerSecond);
            var localProcessScore = process.IdleScore - formalWorkingSetBonus + localWorkingSetBonus -
                                    formalCpuBonus - formalIoBonus +
                                    SmoothCpuBonus(process.CpuPercent) + SmoothIoBonus(process.IoBytesPerSecond);
            return Math.Clamp(localProcessScore, 0, 100) * Math.Max(1, process.WorkingSetBytes);
        }) / totalWeight;

        var idleMinutes = Math.Max(0, idleFor.TotalMinutes);
        var idleBonus = idleMinutes < 15
            ? 0d
            : idleMinutes < 30
                ? Interpolate(idleMinutes, 15, 30, 2.5, 3.5)
                : idleMinutes < 60
                    ? Interpolate(idleMinutes, 30, 60, 3.5, 5)
                    : 5d;
        return Math.Clamp(score + idleBonus, 0, 100);
    }

    private static double FormalCpuBonus(double cpu) => Math.Max(0, cpu) switch
    {
        <= 0.5 => 25,
        <= 2 => 15,
        > 8 => -20,
        _ => 0
    };

    private static double SmoothCpuBonus(double cpu)
    {
        cpu = Math.Max(0, cpu);
        if (cpu <= 0.5) return Interpolate(cpu, 0, 0.5, 25, 22);
        if (cpu <= 2) return Interpolate(cpu, 0.5, 2, 22, 13);
        if (cpu <= 8) return Interpolate(cpu, 2, 8, 13, 0);
        return Interpolate(Math.Min(cpu, 12), 8, 12, 0, -20);
    }

    private static double FormalIoBonus(double io) => Math.Max(0, io) switch
    {
        <= 32d * 1024 => 12,
        <= 256d * 1024 => 6,
        >= 2d * 1024 * 1024 => -18,
        _ => 0
    };

    private static double SmoothIoBonus(double io)
    {
        io = Math.Max(0, io);
        if (io <= 32d * 1024) return Interpolate(io, 0, 32d * 1024, 12, 11);
        if (io <= 256d * 1024) return Interpolate(io, 32d * 1024, 256d * 1024, 11, 6);
        if (io <= 2d * 1024 * 1024) return Interpolate(io, 256d * 1024, 2d * 1024 * 1024, 6, 0);
        return Interpolate(Math.Min(io, 4d * 1024 * 1024), 2d * 1024 * 1024, 4d * 1024 * 1024, 0, -18);
    }

    private static double Interpolate(double value, double low, double high, double lowValue, double highValue)
    {
        if (high <= low) return lowValue;
        var fraction = Math.Clamp((value - low) / (high - low), 0, 1);
        return lowValue + (highValue - lowValue) * fraction;
    }
}
