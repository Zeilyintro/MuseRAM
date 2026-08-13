using System.IO;
using System.Text.Json;

namespace MuseRAM.App;

public enum OptimizationRunKind
{
    Manual,
    Automatic,
    Scheduled,
    LongIdle,
    ApplicationRule,
    GlobalReclaim
}

public enum ReboundObservationState
{
    Observing,
    Completed,
    Replaced
}

public sealed record ReboundHistoryDetail(
    string DisplayName,
    long ReleasedBytes,
    long RegainedBytes);

public sealed record ReboundHistoryRun(
    int Sequence,
    OptimizationRunKind Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    ReboundObservationState State,
    IReadOnlyList<ReboundHistoryDetail> Details);

public sealed record ReboundHistoryDocument(
    int SchemaVersion,
    DateTimeOffset SavedAtUtc,
    IReadOnlyList<ReboundHistoryRun> Runs);

public sealed record ReboundHistoryLoadResult(
    ReboundHistoryDocument? History,
    string? ErrorMessage);

public sealed class ReboundHistoryStore
{
    public const int CurrentSchemaVersion = 1;
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(15);
    private const int MaximumDetailsPerRun = 512;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public ReboundHistoryStore(string? path = null)
    {
        _path = path ?? AppDataPaths.ReboundHistoryFile;
    }

    public string HistoryFile => _path;

    public ReboundHistoryLoadResult LoadWithStatus(DateTimeOffset now)
    {
        if (!File.Exists(_path)) return new ReboundHistoryLoadResult(null, null);
        try
        {
            var document = JsonSerializer.Deserialize<ReboundHistoryDocument>(
                File.ReadAllText(_path),
                JsonOptions) ?? throw new InvalidDataException("Rebound history is empty.");
            if (document.SchemaVersion > CurrentSchemaVersion)
                throw new InvalidDataException($"Rebound history version {document.SchemaVersion} is newer than supported version {CurrentSchemaVersion}.");
            if (document.SchemaVersion <= 0 || document.SavedAtUtc == default)
                throw new InvalidDataException("Rebound history metadata is invalid.");
            var age = now - document.SavedAtUtc;
            if (age < TimeSpan.FromMinutes(-1))
                throw new InvalidDataException("Rebound history timestamp is in the future.");
            if (age > MaximumSnapshotAge)
                return new ReboundHistoryLoadResult(null, null);
            return new ReboundHistoryLoadResult(Normalize(document), null);
        }
        catch (Exception exception)
        {
            return new ReboundHistoryLoadResult(null, exception.Message);
        }
    }

    public void Save(IReadOnlyList<ReboundHistoryRun> runs, DateTimeOffset now)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var document = Normalize(new ReboundHistoryDocument(CurrentSchemaVersion, now, runs));
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, _path, true);
    }

    private static ReboundHistoryDocument Normalize(ReboundHistoryDocument document) => document with
    {
        SchemaVersion = CurrentSchemaVersion,
        Runs = (document.Runs ?? Array.Empty<ReboundHistoryRun>())
            .Where(run => run.Sequence > 0 &&
                          Enum.IsDefined(run.Kind) &&
                          run.StartedAt != default &&
                          Enum.IsDefined(run.State))
            .Select(run => run with
            {
                FinishedAt = run.FinishedAt is { } finishedAt && finishedAt >= run.StartedAt
                    ? finishedAt
                    : null,
                Details = (run.Details ?? Array.Empty<ReboundHistoryDetail>())
                    .Where(detail => !string.IsNullOrWhiteSpace(detail.DisplayName) &&
                                     detail.ReleasedBytes > 0)
                    .Select(detail => detail with
                    {
                        DisplayName = detail.DisplayName.Trim(),
                        RegainedBytes = Math.Clamp(detail.RegainedBytes, 0, detail.ReleasedBytes)
                    })
                    .Take(MaximumDetailsPerRun)
                    .ToArray()
            })
            .Where(run => run.Details.Count > 0)
            .OrderByDescending(run => run.Sequence)
            .ToArray()
    };
}
