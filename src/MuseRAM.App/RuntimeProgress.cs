using System.IO;
using System.Text.Json;
using MuseRAM.Core;

namespace MuseRAM.App;

public static class RuntimeProgressPolicy
{
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumRestorableDuration = TimeSpan.FromDays(36500);

    public static double ElapsedSeconds(DateTimeOffset anchor, DateTimeOffset now) =>
        Math.Max(0, (now - anchor).TotalSeconds);

    public static DateTimeOffset RestoreAnchor(double elapsedSeconds, DateTimeOffset now) =>
        now - RestoreDuration(elapsedSeconds);

    public static TimeSpan RestoreDuration(double elapsedSeconds) =>
        TimeSpan.FromSeconds(NormalizeDurationSeconds(elapsedSeconds));

    public static double NormalizeDurationSeconds(double elapsedSeconds) =>
        double.IsFinite(elapsedSeconds)
            ? Math.Clamp(elapsedSeconds, 0, MaximumRestorableDuration.TotalSeconds)
            : 0;
}

public sealed record RuntimeActivityProgress(
    string FamilyKey,
    int AnchorProcessId,
    long AnchorProcessStartTimeFileTimeUtc,
    double ObservedSeconds,
    double IdleSeconds,
    int SampleCount);

public sealed record RuntimeTrimProgress(
    int ProcessId,
    long ProcessStartTimeFileTimeUtc,
    double ElapsedSeconds);

public sealed record RuntimeProgressDocument(
    int SchemaVersion,
    DateTimeOffset SavedAtUtc,
    double ScheduledOptimizationElapsedSeconds,
    double LastSuccessfulOptimizationElapsedSeconds,
    double? AutomaticSafetyElapsedSeconds,
    long CumulativeTrimBytes,
    long CumulativeNetGainBytes,
    IReadOnlyList<RuntimeActivityProgress> Activities,
    IReadOnlyList<RuntimeTrimProgress> TrimHistory,
    IReadOnlyList<ApplicationBackoffProgress> Backoffs,
    double SessionUptimeSeconds = 0,
    IReadOnlyList<ApplicationOptimizationRuleTargetProgress>? ApplicationRuleTargets = null,
    IReadOnlyList<NaturalStableObservationProgress>? NaturalStableObservations = null);

public sealed record RuntimeProgressLoadResult(
    RuntimeProgressDocument? Progress,
    string? ErrorMessage);

public sealed class RuntimeProgressStore
{
    public const int CurrentSchemaVersion = 4;
    private const int MaximumEntries = 512;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public RuntimeProgressStore(string? path = null)
    {
        _path = path ?? AppDataPaths.RuntimeProgressFile;
    }

    public string ProgressFile => _path;

    public RuntimeProgressLoadResult LoadWithStatus(DateTimeOffset now)
    {
        if (!File.Exists(_path)) return new RuntimeProgressLoadResult(null, null);
        try
        {
            var progress = JsonSerializer.Deserialize<RuntimeProgressDocument>(
                File.ReadAllText(_path),
                JsonOptions) ?? throw new InvalidDataException("Runtime progress is empty.");
            if (progress.SchemaVersion > CurrentSchemaVersion)
                throw new InvalidDataException($"Runtime progress version {progress.SchemaVersion} is newer than supported version {CurrentSchemaVersion}.");
            if (progress.SchemaVersion <= 0 || progress.SavedAtUtc == default)
                throw new InvalidDataException("Runtime progress metadata is invalid.");
            var age = now - progress.SavedAtUtc;
            if (age < TimeSpan.FromMinutes(-1))
                throw new InvalidDataException("Runtime progress timestamp is in the future.");
            if (age > RuntimeProgressPolicy.MaximumSnapshotAge)
                return new RuntimeProgressLoadResult(null, null);
            return new RuntimeProgressLoadResult(Normalize(progress), null);
        }
        catch (Exception exception)
        {
            return new RuntimeProgressLoadResult(null, exception.Message);
        }
    }

    public void Save(RuntimeProgressDocument progress)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Normalize(progress), JsonOptions));
        File.Move(temporaryPath, _path, true);
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
        var temporaryPath = _path + ".tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }

    private static RuntimeProgressDocument Normalize(RuntimeProgressDocument progress) => progress with
    {
        SchemaVersion = CurrentSchemaVersion,
        ScheduledOptimizationElapsedSeconds = RuntimeProgressPolicy.NormalizeDurationSeconds(progress.ScheduledOptimizationElapsedSeconds),
        LastSuccessfulOptimizationElapsedSeconds = RuntimeProgressPolicy.NormalizeDurationSeconds(progress.LastSuccessfulOptimizationElapsedSeconds),
        AutomaticSafetyElapsedSeconds = progress.AutomaticSafetyElapsedSeconds is { } safety
            ? RuntimeProgressPolicy.NormalizeDurationSeconds(safety)
            : null,
        SessionUptimeSeconds = RuntimeProgressPolicy.NormalizeDurationSeconds(progress.SessionUptimeSeconds),
        CumulativeTrimBytes = Math.Max(0, progress.CumulativeTrimBytes),
        Activities = (progress.Activities ?? Array.Empty<RuntimeActivityProgress>())
            .Where(item => !string.IsNullOrWhiteSpace(item.FamilyKey) &&
                           item.AnchorProcessId > 0 && item.AnchorProcessStartTimeFileTimeUtc > 0 &&
                           double.IsFinite(item.ObservedSeconds) && item.ObservedSeconds >= 0 &&
                           double.IsFinite(item.IdleSeconds) && item.IdleSeconds >= 0 &&
                           item.SampleCount >= 0)
            .Select(item => item with
            {
                ObservedSeconds = RuntimeProgressPolicy.NormalizeDurationSeconds(item.ObservedSeconds),
                IdleSeconds = RuntimeProgressPolicy.NormalizeDurationSeconds(item.IdleSeconds)
            })
            .Take(MaximumEntries)
            .ToArray(),
        TrimHistory = (progress.TrimHistory ?? Array.Empty<RuntimeTrimProgress>())
            .Where(item => item.ProcessId > 0 && item.ProcessStartTimeFileTimeUtc > 0 &&
                           double.IsFinite(item.ElapsedSeconds) && item.ElapsedSeconds >= 0)
            .Select(item => item with
            {
                ElapsedSeconds = RuntimeProgressPolicy.NormalizeDurationSeconds(item.ElapsedSeconds)
            })
            .Take(MaximumEntries)
            .ToArray(),
        Backoffs = (progress.Backoffs ?? Array.Empty<ApplicationBackoffProgress>())
            .Where(item => !string.IsNullOrWhiteSpace(item.FamilyKey) &&
                           item.ReboundCount > 0 &&
                           double.IsFinite(item.RemainingBlockSeconds) && item.RemainingBlockSeconds >= 0 &&
                           (item.LongTermObservedSeconds is null ||
                            double.IsFinite(item.LongTermObservedSeconds.Value) &&
                            item.LongTermObservedSeconds.Value >= 0))
            .Select(item => item with
            {
                RemainingBlockSeconds = RuntimeProgressPolicy.NormalizeDurationSeconds(item.RemainingBlockSeconds),
                LongTermObservedSeconds = item.LongTermObservedSeconds is { } observed
                    ? RuntimeProgressPolicy.NormalizeDurationSeconds(observed)
                    : null
            })
            .ToArray(),
        ApplicationRuleTargets = (progress.ApplicationRuleTargets ??
            Array.Empty<ApplicationOptimizationRuleTargetProgress>())
            .Where(item => !string.IsNullOrWhiteSpace(item.RuleId) &&
                           !string.IsNullOrWhiteSpace(item.TargetIdentity) &&
                           !string.IsNullOrWhiteSpace(item.ConfigurationKey) &&
                           !string.IsNullOrWhiteSpace(item.LaunchSignature) &&
                           item.ConfigurationRevision > 0)
            .Select(item => item with
            {
                DelayExecutionsCompleted = Math.Max(0, item.DelayExecutionsCompleted),
                LastDelayExecutionElapsedSeconds = NormalizeOptionalDuration(item.LastDelayExecutionElapsedSeconds),
                LastExecutionStartedElapsedSeconds = NormalizeOptionalDuration(item.LastExecutionStartedElapsedSeconds),
                LastReleasedBytes = Math.Max(0, item.LastReleasedBytes),
                LastRetainedBytes = item.LastRetainedBytes is { } retained ? Math.Max(0, retained) : null,
                Processes = (item.Processes ?? Array.Empty<ApplicationOptimizationRuleProcessProgress>())
                    .Where(process => !string.IsNullOrWhiteSpace(process.ProcessIdentity))
                    .Select(process => process with
                    {
                        LastSuccessfulTrimElapsedSeconds = NormalizeOptionalDuration(
                            process.LastSuccessfulTrimElapsedSeconds),
                        LastWorkingSetExecutionElapsedSeconds = NormalizeOptionalDuration(
                            process.LastWorkingSetExecutionElapsedSeconds)
                    })
                    .Take(MaximumEntries)
                    .ToArray()
            })
            .Take(MaximumEntries)
            .ToArray(),
        NaturalStableObservations = (progress.NaturalStableObservations ??
                                     Array.Empty<NaturalStableObservationProgress>())
            .Where(item => !string.IsNullOrWhiteSpace(item.FamilyKey) &&
                           !string.IsNullOrWhiteSpace(item.ScopeKey) &&
                           !string.IsNullOrWhiteSpace(item.LaunchSignature) &&
                           item.StartedAt != default && item.LastObservedAt != default)
            .Select(item => item with
            {
                ComponentKeys = (item.ComponentKeys ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumEntries)
                    .ToArray(),
                WorkingSetSamples = (item.WorkingSetSamples ??
                                     Array.Empty<NaturalStableTimedSampleProgress>())
                    .Where(sample => sample.ObservedAt != default && sample.WorkingSetBytes > 0)
                    .TakeLast(MaximumEntries)
                    .ToArray()
            })
            .Where(item => item.ComponentKeys.Count > 0 && item.WorkingSetSamples.Count > 0)
            .Take(MaximumEntries)
            .ToArray()
    };

    private static double? NormalizeOptionalDuration(double? duration) => duration is { } value
        ? RuntimeProgressPolicy.NormalizeDurationSeconds(value)
        : null;
}
