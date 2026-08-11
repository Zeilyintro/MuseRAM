using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MuseRAM.Core;

namespace MuseRAM.App;

public sealed record WindowsServiceDescriptor(
    string Name,
    string DisplayName,
    string? ExecutablePath,
    bool IsRunning,
    bool IsSystemService,
    int ProcessId = 0,
    bool CanStop = true);

public sealed record ServiceSuggestion(
    WindowsServiceDescriptor Service,
    string RelatedApplication,
    bool IsRecommended,
    string Impact,
    string? ImpactResourceKey = null);

public sealed record ServiceStopResult(string ServiceName, bool Success, string? Error);

public enum WindowsServiceRuntimeState : uint
{
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7
}

public readonly record struct WindowsServiceStatusQuery(
    bool Success,
    WindowsServiceRuntimeState State,
    int ErrorCode = 0);

public static class ServiceStopVerificationPolicy
{
    public const int DefaultMaximumChecks = 50;
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static ServiceStopResult Verify(
        string serviceName,
        Func<WindowsServiceStatusQuery> queryStatus,
        Action<TimeSpan> wait,
        int maximumChecks = DefaultMaximumChecks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(queryStatus);
        ArgumentNullException.ThrowIfNull(wait);
        if (maximumChecks <= 0) throw new ArgumentOutOfRangeException(nameof(maximumChecks));

        var lastState = default(WindowsServiceRuntimeState);
        for (var check = 0; check < maximumChecks; check++)
        {
            var status = queryStatus();
            if (!status.Success)
            {
                return new ServiceStopResult(
                    serviceName,
                    false,
                    $"QueryServiceStatusEx failed ({status.ErrorCode}).");
            }

            lastState = status.State;
            if (lastState == WindowsServiceRuntimeState.Stopped)
                return new ServiceStopResult(serviceName, true, null);
            if (check + 1 < maximumChecks) wait(PollInterval);
        }

        return new ServiceStopResult(
            serviceName,
            false,
            $"Service did not stop before the verification timeout (last state: {lastState}).");
    }
}

public static class RelatedServiceAdvisor
{
    private static readonly ServiceDefinition[] KnownTargets =
    {
        new("DiagTrack", "Connected User Experiences and Telemetry", false, "仅在不需要遥测功能时停止。", "ServiceImpactTelemetry"),
        new("DmWappushService", "WAP Push Message Routing", false, "仅在不需要相关消息功能时停止。", "ServiceImpactMessages"),
        new("CDPSvc", "Connected Devices Platform", false, "仅在不使用跨设备功能时停止。", "ServiceImpactDevices"),
        new("CDPUserSvc", "Connected Devices Platform User Service", false, "仅在不使用跨设备功能时停止。", "ServiceImpactDevices"),
        new("PimIndexMaintenanceSvc", "Contact Data Indexing", false, "仅在不需要联系人索引时停止。", "ServiceImpactContacts"),
        new("CopilotService", "Microsoft Copilot Service", true, "与 Copilot 应用一起停止。", "ServiceImpactCopilot"),
        new("WSearch", "Windows Search", false, "建议保持运行；停止后会影响 Windows 搜索和索引。", "ServiceImpactSearch")
    };

    public static IReadOnlyList<ServiceSuggestion> Find(
        IReadOnlyList<ProcessFamilySnapshot> selectedFamilies,
        IReadOnlyList<WindowsServiceDescriptor> services)
    {
        var applications = selectedFamilies
            .Select(family => new ApplicationPaths(
                family.DisplayName,
                family.Processes.Select(process => process.ProcessId).ToHashSet(),
                family.Processes
                    .Select(process => NormalizePath(process.ExecutablePath))
                    .Where(path => path is not null)
                    .Select(path => path!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                family.Processes
                    .Select(process => NormalizeDirectory(process.ExecutablePath))
                    .Where(path => path is not null)
                    .Select(path => path!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToArray();

        return services
            .Where(service => service.IsRunning && service.CanStop)
            .Select(service => (Service: service, Match: FindMatch(service, applications)))
            .Where(item => item.Match is not null || FindKnownTarget(item.Service.Name) is not null)
            .Select(item => new ServiceSuggestion(
                item.Service,
                item.Match?.DisplayName ?? "Windows",
                IsRecommended: false,
                Impact: BuildImpact(item.Service, item.Match is not null),
                ImpactResourceKey: BuildImpactResourceKey(item.Service, item.Match is not null)))
            .OrderBy(suggestion => FindKnownTarget(suggestion.Service.Name)?.IsApplication == true ? 0 : 1)
            .ThenBy(suggestion => suggestion.Service.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static ApplicationPaths? FindMatch(
        WindowsServiceDescriptor service,
        IReadOnlyList<ApplicationPaths> applications)
    {
        var servicePath = NormalizePath(service.ExecutablePath);
        var serviceDirectory = servicePath is null ? null : Path.GetDirectoryName(servicePath);
        var normalizedServiceName = NormalizeName(service.Name);

        return applications.FirstOrDefault(application =>
            (service.ProcessId > 0 && application.ProcessIds.Contains(service.ProcessId)) ||
            (servicePath is not null && application.ExecutablePaths.Contains(servicePath)) ||
            (!string.IsNullOrWhiteSpace(serviceDirectory) && application.Directories.Contains(serviceDirectory)) ||
            (NormalizeName(application.DisplayName).Length >= 4 &&
             normalizedServiceName.StartsWith(NormalizeName(application.DisplayName), StringComparison.OrdinalIgnoreCase)));
    }

    private static ServiceDefinition? FindKnownTarget(string serviceName) => KnownTargets.FirstOrDefault(target =>
        serviceName.Equals(target.NamePrefix, StringComparison.OrdinalIgnoreCase) ||
        serviceName.StartsWith(target.NamePrefix + "_", StringComparison.OrdinalIgnoreCase));

    private static string BuildImpact(WindowsServiceDescriptor service, bool relatedToApplication)
    {
        var known = FindKnownTarget(service.Name);
        if (known is not null) return known.Impact;
        if (service.IsSystemService) return "系统服务；停止后可能影响 Windows 或其他应用。";
        return relatedToApplication
            ? "应用后台服务；停止后相关后台功能将不可用，直到服务或应用重新启动。"
            : "系统服务；停止后可能影响 Windows 或其他应用。";
    }

    private static string BuildImpactResourceKey(WindowsServiceDescriptor service, bool relatedToApplication) =>
        FindKnownTarget(service.Name)?.ImpactResourceKey ??
        (service.IsSystemService || !relatedToApplication ? "ServiceImpactSystem" : "ServiceImpactApplication");

    private static string NormalizeName(string value)
    {
        var normalized = value.Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? normalized[..^4] : normalized;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return null; }
    }

    private static string? NormalizeDirectory(string? executablePath)
    {
        var path = NormalizePath(executablePath);
        return path is null ? null : Path.GetDirectoryName(path);
    }

    private sealed record ApplicationPaths(
        string DisplayName,
        IReadOnlySet<int> ProcessIds,
        IReadOnlySet<string> ExecutablePaths,
        IReadOnlySet<string> Directories);

    private sealed record ServiceDefinition(
        string NamePrefix,
        string DisplayName,
        bool IsApplication,
        string Impact,
        string ImpactResourceKey);
}

public static class DeepReleaseCandidateDeduplicator
{
    public static IReadOnlyList<DeepReleaseCandidate> RemoveServiceDuplicates(
        IReadOnlyList<DeepReleaseCandidate> applications,
        IReadOnlyList<ServiceSuggestion> services)
    {
        var serviceProcessIds = services
            .Where(suggestion => suggestion.Service.ProcessId > 0)
            .Select(suggestion => suggestion.Service.ProcessId)
            .ToHashSet();
        var serviceNames = services
            .Select(suggestion => suggestion.Service.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return applications.Where(candidate =>
            !serviceNames.Contains(candidate.Family.DisplayName) &&
            !candidate.Family.Processes.Select(process => process.ProcessId).All(serviceProcessIds.Contains))
            .ToArray();
    }
}

public static class WindowsServiceCommandLine
{
    public static string? ExtractExecutablePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        string candidate;
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote <= 1) return null;
            candidate = expanded[1..closingQuote];
        }
        else
        {
            var executableEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = executableEnd >= 0
                ? expanded[..(executableEnd + 4)]
                : expanded.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        }

        candidate = candidate.Trim();
        if (candidate.StartsWith(@"\??\", StringComparison.Ordinal)) candidate = candidate[4..];
        try { return candidate.Length == 0 ? null : Path.GetFullPath(candidate); }
        catch { return null; }
    }
}

public sealed class WindowsServiceManager
{
    public IReadOnlyList<WindowsServiceDescriptor> CaptureRunningServices()
    {
        var result = new List<WindowsServiceDescriptor>();
        var manager = Native.OpenSCManager(null, null, Native.ScManagerConnect);
        if (manager == IntPtr.Zero) return result;

        try
        {
            using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey is null) return result;
            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var serviceKey = servicesKey.OpenSubKey(serviceName);
                    if (serviceKey is null) continue;
                    var imagePath = WindowsServiceCommandLine.ExtractExecutablePath(serviceKey.GetValue("ImagePath") as string);
                    if (!TryGetRunningStatus(manager, serviceName, out var status)) continue;
                    var displayName = serviceKey.GetValue("DisplayName") as string;
                    result.Add(new WindowsServiceDescriptor(
                        serviceName,
                        string.IsNullOrWhiteSpace(displayName) ? serviceName : displayName,
                        imagePath,
                        true,
                        IsSystemPath(imagePath),
                        status.ProcessId <= int.MaxValue ? (int)status.ProcessId : 0,
                        (status.ControlsAccepted & Native.ServiceAcceptStop) != 0));
                }
                catch
                {
                    // Registry entries can disappear or be inaccessible while enumerating.
                }
            }
        }
        finally
        {
            Native.CloseServiceHandle(manager);
        }

        return result;
    }

    public ServiceStopResult Stop(string serviceName)
    {
        var manager = Native.OpenSCManager(null, null, Native.ScManagerConnect);
        if (manager == IntPtr.Zero)
            return new ServiceStopResult(serviceName, false, $"OpenSCManager failed ({Marshal.GetLastWin32Error()}).");

        try
        {
            var service = Native.OpenService(manager, serviceName, Native.ServiceStop | Native.ServiceQueryStatus);
            if (service == IntPtr.Zero)
                return new ServiceStopResult(serviceName, false, $"OpenService failed ({Marshal.GetLastWin32Error()}).");

            try
            {
                var initialStatus = ReadStatus(service);
                if (!initialStatus.Success)
                    return StatusQueryFailure(serviceName, initialStatus.ErrorCode);
                if (initialStatus.State == WindowsServiceRuntimeState.Stopped)
                    return new ServiceStopResult(serviceName, true, null);

                if (initialStatus.State != WindowsServiceRuntimeState.StopPending &&
                    !Native.ControlService(service, Native.ServiceControlStop, out _))
                {
                    var controlError = Marshal.GetLastWin32Error();
                    var statusAfterFailure = ReadStatus(service);
                    if (statusAfterFailure.Success &&
                        statusAfterFailure.State == WindowsServiceRuntimeState.Stopped)
                    {
                        return new ServiceStopResult(serviceName, true, null);
                    }
                    return new ServiceStopResult(
                        serviceName,
                        false,
                        $"ControlService failed ({controlError}).");
                }

                return ServiceStopVerificationPolicy.Verify(
                    serviceName,
                    () => ReadStatus(service),
                    delay => Thread.Sleep(delay));
            }
            finally
            {
                Native.CloseServiceHandle(service);
            }
        }
        finally
        {
            Native.CloseServiceHandle(manager);
        }
    }

    private static bool TryGetRunningStatus(
        IntPtr manager,
        string serviceName,
        out Native.ServiceStatusProcess status)
    {
        status = default;
        var service = Native.OpenService(manager, serviceName, Native.ServiceQueryStatus);
        if (service == IntPtr.Zero) return false;
        try
        {
            var query = TryReadStatus(service, out status);
            return query.Success && status.CurrentState == Native.ServiceRunning;
        }
        finally
        {
            Native.CloseServiceHandle(service);
        }
    }

    private static WindowsServiceStatusQuery ReadStatus(IntPtr service)
    {
        var result = TryReadStatus(service, out var status);
        return result.Success
            ? new WindowsServiceStatusQuery(
                true,
                (WindowsServiceRuntimeState)status.CurrentState)
            : result;
    }

    private static WindowsServiceStatusQuery TryReadStatus(
        IntPtr service,
        out Native.ServiceStatusProcess status)
    {
        var size = (uint)Marshal.SizeOf<Native.ServiceStatusProcess>();
        if (Native.QueryServiceStatusEx(service, 0, out status, size, out _))
        {
            return new WindowsServiceStatusQuery(
                true,
                (WindowsServiceRuntimeState)status.CurrentState);
        }

        return new WindowsServiceStatusQuery(
            false,
            default,
            Marshal.GetLastWin32Error());
    }

    private static ServiceStopResult StatusQueryFailure(string serviceName, int errorCode) =>
        new(serviceName, false, $"QueryServiceStatusEx failed ({errorCode}).");

    private static bool IsSystemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows)) return false;
        try
        {
            var normalizedWindows = Path.GetFullPath(windows).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(normalizedWindows, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static class Native
    {
        internal const uint ScManagerConnect = 0x0001;
        internal const uint ServiceQueryStatus = 0x0004;
        internal const uint ServiceStop = 0x0020;
        internal const uint ServiceControlStop = 0x00000001;
        internal const uint ServiceRunning = 0x00000004;
        internal const uint ServiceAcceptStop = 0x00000001;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(
            IntPtr service,
            int infoLevel,
            out ServiceStatusProcess status,
            uint bufferSize,
            out uint bytesNeeded);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatus
        {
            internal uint ServiceType;
            internal uint CurrentState;
            internal uint ControlsAccepted;
            internal uint Win32ExitCode;
            internal uint ServiceSpecificExitCode;
            internal uint CheckPoint;
            internal uint WaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatusProcess
        {
            internal uint ServiceType;
            internal uint CurrentState;
            internal uint ControlsAccepted;
            internal uint Win32ExitCode;
            internal uint ServiceSpecificExitCode;
            internal uint CheckPoint;
            internal uint WaitHint;
            internal uint ProcessId;
            internal uint ServiceFlags;
        }
    }
}
