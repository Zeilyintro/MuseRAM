namespace MuseRAM.Core;

public static class SystemProcessPolicy
{
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "svchost", "ntoskrnl", "smss",
        "csrss", "wininit", "winlogon", "services", "lsass", "fontdrvhost", "dwm", "sihost",
        "spoolsv", "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "RuntimeBroker",
        "SearchHost", "taskhostw", "ctfmon", "audiodg", "WmiPrvSE", "ApplicationFrameHost",
        "SystemSettings", "LockApp", "TextInputHost", "backgroundTaskHost", "UserOOBEBroker",
        "SecurityHealthService", "SecurityHealthSystray", "conhost", "dllhost", "WUDFHost",
        "dasHost", "unsecapp", "msedgewebview2", "HipsDaemon", "HipsTray", "MsMpEng",
        "NisSrv", "MpDefenderCoreService", "Sense", "avp", "AVGSvc", "AvastSvc", "360tray",
        "360safe", "ZhuDongFangYu", "QQPCRTP"
    };

    public static bool IsAlwaysExcluded(string processName, string? executablePath)
    {
        _ = executablePath;
        return ExcludedNames.Contains(NormalizeName(processName));
    }

    private static string NormalizeName(string value)
    {
        var normalized = value.Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }
}

public static class ExecutablePathIdentity
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path.Trim());
    }

    public static bool TryNormalize(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            normalizedPath = Normalize(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public sealed class ApplicationProtectionRule
{
    public string ApplicationExecutablePath { get; set; } = string.Empty;
    public bool ProtectEntireFamily { get; set; }
    public List<string> ProtectedExecutablePaths { get; set; } = new();
}

public sealed class ProtectionContext
{
    internal ProtectionContext(
        IReadOnlySet<int> relatedProcessIds,
        IReadOnlySet<string> titleTokens)
    {
        RelatedProcessIds = relatedProcessIds;
        TitleTokens = titleTokens;
    }

    internal IReadOnlySet<int> RelatedProcessIds { get; }
    internal IReadOnlySet<string> TitleTokens { get; }
}

public sealed class ProtectionRules
{
    private const int MinimumTitleTokenLength = 6;
    private static readonly HashSet<string> GenericTitleTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "client", "game", "helper", "launcher", "setup", "update", "updater"
    };
    private static readonly HashSet<string> BrowserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "brave", "chrome", "firefox", "iexplore", "msedge", "opera", "vivaldi"
    };
    private static readonly HashSet<string> RelatedWindowHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost", "bootstrapper", "client", "helper", "host", "launcher",
        "setup", "update", "updater"
    };

    private readonly HashSet<string> _exactPaths;
    private readonly HashSet<string> _wholeFamilyPaths;
    private readonly HashSet<string> _wholeFamilyNames;

    public ProtectionRules(
        IEnumerable<string>? executablePaths = null,
        bool protectRelatedProcesses = true)
        : this((executablePaths ?? Array.Empty<string>()).Select(path =>
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = path,
                ProtectEntireFamily = protectRelatedProcesses,
                ProtectedExecutablePaths = protectRelatedProcesses
                    ? new List<string>()
                    : new List<string> { path }
            }))
    {
    }

    public ProtectionRules(IEnumerable<ApplicationProtectionRule> applicationRules)
    {
        ArgumentNullException.ThrowIfNull(applicationRules);

        _exactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _wholeFamilyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in applicationRules.Where(rule => rule is not null))
        {
            if (!ExecutablePathIdentity.TryNormalize(rule.ApplicationExecutablePath, out var applicationPath))
                continue;

            if (rule.ProtectEntireFamily)
            {
                _wholeFamilyPaths.Add(applicationPath);
                _exactPaths.Add(applicationPath);
                continue;
            }

            foreach (var path in rule.ProtectedExecutablePaths ?? new List<string>())
            {
                if (ExecutablePathIdentity.TryNormalize(path, out var protectedPath))
                    _exactPaths.Add(protectedPath);
            }
        }

        _wholeFamilyNames = _wholeFamilyPaths
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public ProtectionContext CreateContext(IEnumerable<ProcessSnapshot> processes)
    {
        var snapshots = processes.ToArray();
        var relatedProcessIds = snapshots
            .Where(MatchesProtectedSuite)
            .Select(process => process.ProcessId)
            .ToHashSet();

        return new ProtectionContext(relatedProcessIds, BuildTitleTokens());
    }

    public bool IsProtected(ProcessFamilySnapshot family) =>
        IsProtected(family, CreateContext(family.Processes));

    public bool IsProtected(ProcessFamilySnapshot family, ProtectionContext context) =>
        family.Processes.Any(process => MatchesExactPath(process) ||
            MatchesWholeFamilyPath(process) ||
            MatchesWholeFamilyName(process) ||
            context.RelatedProcessIds.Contains(process.ProcessId) ||
            MatchesRelatedWindow(process, context.TitleTokens));

    public ProcessFamilySnapshot? FilterUnprotectedProcesses(
        ProcessFamilySnapshot family,
        ProtectionContext context)
    {
        if (IsWholeFamilyProtected(family, context)) return null;
        var remaining = family.Processes.Where(process => !MatchesExactPath(process)).ToArray();
        return remaining.Length == 0
            ? null
            : new ProcessFamilySnapshot(family.Key, family.DisplayName, family.ExecutableDirectory, remaining);
    }

    public bool MatchesExactPath(ProcessSnapshot process) =>
        ExecutablePathIdentity.TryNormalize(process.ExecutablePath, out var path) &&
        _exactPaths.Contains(path);

    private bool IsWholeFamilyProtected(ProcessFamilySnapshot family, ProtectionContext context) =>
        family.Processes.Any(process => MatchesWholeFamilyPath(process) ||
            MatchesWholeFamilyName(process) ||
            context.RelatedProcessIds.Contains(process.ProcessId) ||
            MatchesRelatedWindow(process, context.TitleTokens));

    private bool MatchesWholeFamilyPath(ProcessSnapshot process) =>
        ExecutablePathIdentity.TryNormalize(process.ExecutablePath, out var path) &&
        _wholeFamilyPaths.Contains(path);

    private bool MatchesWholeFamilyName(ProcessSnapshot process) =>
        _wholeFamilyNames.Contains(NormalizeName(process.Name));

    private bool MatchesProtectedSuite(ProcessSnapshot process)
    {
        if (string.IsNullOrWhiteSpace(process.ExecutablePath)) return false;
        return _wholeFamilyPaths.Any(protectedPath => AreSuiteComponents(
            protectedPath,
            Path.GetFileNameWithoutExtension(protectedPath),
            process.ExecutablePath,
            process.Name));
    }

    private static bool AreSuiteComponents(
        string protectedPath,
        string protectedName,
        string candidatePath,
        string candidateName)
    {
        var namePrefix = CommonPrefix(
            NormalizeSearchToken(protectedName),
            NormalizeSearchToken(candidateName));
        if (namePrefix.Length < 6 || GenericSuitePrefixes.Any(prefix =>
                namePrefix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var commonDirectory = CommonDirectory(
            Path.GetDirectoryName(protectedPath),
            Path.GetDirectoryName(candidatePath));
        return commonDirectory is not null && IsSpecificProductDirectory(commonDirectory);
    }

    private static readonly HashSet<string> GenericSuitePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application", "client", "helper", "launcher", "service", "setup", "update", "updater"
    };

    private static string CommonPrefix(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && char.ToLowerInvariant(left[index]) == char.ToLowerInvariant(right[index])) index++;
        return left[..index];
    }

    private static string? CommonDirectory(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return null;
        try
        {
            var candidate = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar);
            while (candidate.Length > 0)
            {
                if (target.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
                candidate = Path.GetDirectoryName(candidate)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private static bool IsSpecificProductDirectory(string directory)
    {
        var bases = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), 2),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), 2),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 1),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 1)
        };
        foreach (var (basePath, minimumSegments) in bases.Where(item => !string.IsNullOrWhiteSpace(item.Item1)))
        {
            if (!directory.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            return Path.GetRelativePath(basePath, directory)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Length >= minimumSegments;
        }

        var root = Path.GetPathRoot(directory);
        if (string.IsNullOrWhiteSpace(root)) return false;
        return Path.GetRelativePath(root, directory)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Length >= 2;
    }

    private IReadOnlySet<string> BuildTitleTokens()
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _wholeFamilyNames) AddTitleToken(tokens, name);
        foreach (var path in _wholeFamilyPaths)
        {
            AddTitleToken(tokens, Path.GetFileNameWithoutExtension(path));
            AddTitleToken(tokens, Path.GetFileName(Path.GetDirectoryName(path)));
        }
        return tokens;
    }

    private static void AddTitleToken(ISet<string> tokens, string? value)
    {
        var token = NormalizeSearchToken(value);
        if (token.Length >= MinimumTitleTokenLength && !GenericTitleTokens.Contains(token)) tokens.Add(token);
    }

    private static bool MatchesRelatedWindow(ProcessSnapshot process, IReadOnlySet<string> titleTokens)
    {
        if (!process.HasVisibleWindow ||
            string.IsNullOrWhiteSpace(process.MainWindowTitle) ||
            titleTokens.Count == 0)
        {
            return false;
        }

        var processName = NormalizeName(process.Name);
        if (BrowserNames.Contains(processName) || !IsRelatedWindowHost(processName)) return false;

        var title = NormalizeSearchToken(process.MainWindowTitle);
        return titleTokens.Any(token => title.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRelatedWindowHost(string processName) =>
        RelatedWindowHosts.Contains(processName) ||
        processName.EndsWith("bootstrapper", StringComparison.OrdinalIgnoreCase) ||
        processName.EndsWith("helper", StringComparison.OrdinalIgnoreCase) ||
        processName.EndsWith("host", StringComparison.OrdinalIgnoreCase) ||
        processName.EndsWith("launcher", StringComparison.OrdinalIgnoreCase) ||
        processName.EndsWith("updater", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSearchToken(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizeName(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

}
