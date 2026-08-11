using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace MuseRAM.App;

public enum UpdateCheckFrequency
{
    EveryStartup,
    Daily,
    Weekly,
    ManualOnly
}

public static class UpdateConfiguration
{
    public const string RepositoryUrl = "https://github.com/Zeilyintro/MuseRAM";
    public const string FeedUrl = "https://github.com/Zeilyintro/MuseRAM/releases/latest/download/update.json";
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
}

public static class UpdateCheckPolicy
{
    public static bool IsDue(UpdateCheckFrequency frequency, DateTimeOffset? lastCheck, DateTimeOffset now) =>
        frequency switch
        {
            UpdateCheckFrequency.EveryStartup => true,
            UpdateCheckFrequency.Daily => !lastCheck.HasValue || now - lastCheck.Value >= TimeSpan.FromDays(1),
            UpdateCheckFrequency.Weekly => !lastCheck.HasValue || now - lastCheck.Value >= TimeSpan.FromDays(7),
            _ => false
        };
}

public sealed record UpdateAsset(Version Version, Uri DownloadUri, string Sha256, string FileName);
public sealed record UpdateCheckResult(bool IsAvailable, UpdateAsset? Asset, string Message);

public sealed class UpdateFeedClient
{
    private readonly HttpClient _httpClient;

    public UpdateFeedClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<UpdateCheckResult> CheckAsync(Uri feedUri, Version currentVersion, CancellationToken cancellationToken = default)
    {
        if (!UpdateUriPolicy.IsHttps(feedUri))
            throw new ArgumentException("更新清单必须使用 HTTPS。", nameof(feedUri));

        using var response = await _httpClient.GetAsync(feedUri, cancellationToken).ConfigureAwait(false);
        UpdateUriPolicy.EnsureHttpsResponse(response, "更新清单");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }, cancellationToken).ConfigureAwait(false);
        if (manifest is null ||
            !Version.TryParse(manifest.Version, out var latestVersion) ||
            !Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            !UpdateUriPolicy.IsHttps(downloadUri) ||
            !IsSha256(manifest.Sha256))
        {
            return new UpdateCheckResult(false, null, "更新清单无效。");
        }

        var fileName = Path.GetFileName(downloadUri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "MuseRAM.exe";
        var asset = new UpdateAsset(latestVersion, downloadUri, manifest.Sha256!.ToUpperInvariant(), fileName);
        return latestVersion > currentVersion
            ? new UpdateCheckResult(true, asset, $"发现 MuseRAM {latestVersion}。")
            : new UpdateCheckResult(false, asset, "当前已是最新版本。");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed class UpdateManifest
    {
        public string? Version { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Sha256 { get; set; }
    }
}

public sealed class UpdatePackageDownloader
{
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);
    private readonly HttpClient _httpClient;

    public UpdatePackageDownloader(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<string> DownloadAsync(
        UpdateAsset asset,
        string updateDirectory,
        CancellationToken cancellationToken = default,
        TimeSpan? responseTimeout = null,
        TimeSpan? idleTimeout = null)
    {
        if (!UpdateUriPolicy.IsHttps(asset.DownloadUri))
            throw new ArgumentException("更新包必须使用 HTTPS。", nameof(asset));

        UpdateStorage.EnsureAllowed(updateDirectory);
        Directory.CreateDirectory(updateDirectory);
        var destination = Path.Combine(updateDirectory, $"MuseRAM-{asset.Version}.exe");
        var temporary = destination + ".download";
        try
        {
            using var responseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseCancellation.CancelAfter(responseTimeout ?? DefaultResponseTimeout);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(
                    asset.DownloadUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    responseCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("连接更新服务器超时。");
            }
            using (response)
            {
            UpdateUriPolicy.EnsureHttpsResponse(response, "更新包");
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            var inactivityLimit = idleTimeout ?? DefaultIdleTimeout;
            while (true)
            {
                using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCancellation.CancelAfter(inactivityLimit);
                try
                {
                    read = await source.ReadAsync(buffer, idleCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"更新下载连续 {inactivityLimit.TotalSeconds:0} 秒未收到数据。");
                }
                if (read <= 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
            }
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            var actualHash = hash.GetHashAndReset();
            var expectedHash = Convert.FromHexString(asset.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                throw new InvalidDataException("下载文件的 SHA256 与更新清单不一致。");
            await target.DisposeAsync().ConfigureAwait(false);
            File.Move(temporary, destination, true);
            return destination;
            }
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }
}

public static class UpdateUriPolicy
{
    public static bool IsHttps(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(uri.Host);

    public static void EnsureHttpsResponse(HttpResponseMessage response, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(response);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || !IsHttps(finalUri))
        {
            throw new InvalidDataException($"{resourceName}重定向到了非 HTTPS 地址。");
        }
    }
}

public static class UpdateStorage
{
    public static string Resolve(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredDirectory));
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (current.Name.Equals("MuseRAM-DevTools", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(current.FullName, "MuseRAM-Updates");
            current = current.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "MuseRAM-Updates");
    }

    public static void EnsureAllowed(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (!string.IsNullOrWhiteSpace(systemRoot) &&
            string.Equals(Path.GetPathRoot(fullPath), systemRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新缓存目录不能位于系统盘，请指定其他磁盘。");
    }

    public static int CleanupExpired(string directory, DateTimeOffset now, TimeSpan lifetime)
    {
        if (!Directory.Exists(directory)) return 0;
        EnsureAllowed(directory);
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "MuseRAM-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                  name.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
                  name.EndsWith(".new", StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                if (now - File.GetLastWriteTimeUtc(path) < lifetime) continue;
                File.Delete(path);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    public static bool CleanupRollback(string executablePath)
    {
        var rollback = Path.GetFullPath(executablePath) + ".rollback";
        if (!File.Exists(rollback)) return false;
        try
        {
            File.Delete(rollback);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

public static class UpdateLauncher
{
    public static bool IsSingleExecutableDistribution(string executablePath, string? entryAssemblyLocation)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        if (string.IsNullOrWhiteSpace(entryAssemblyLocation)) return true;

        return PathsEqual(executablePath, entryAssemblyLocation);
    }

    public static void EnsureSupportedCurrentDistribution()
    {
        var executablePath = Environment.ProcessPath;
#pragma warning disable IL3000 // An empty Location is the intended single-file distribution signal.
        var entryAssemblyLocation = Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !IsSingleExecutableDistribution(executablePath, entryAssemblyLocation))
        {
            throw new InvalidOperationException("当前构建依赖多个文件，不能使用单 EXE 原位更新。请使用完整发布包更新。");
        }
    }

    public static void LaunchReplacement(string stagedExecutable)
    {
        EnsureSupportedCurrentDistribution();
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var staged = Path.GetFullPath(stagedExecutable);
        if (!File.Exists(staged)) throw new FileNotFoundException("找不到已暂存的更新程序。", staged);
        var updateDirectory = Path.GetDirectoryName(staged) ?? throw new InvalidOperationException("无法确定更新暂存目录。");
        UpdateStorage.EnsureAllowed(updateDirectory);

        var startInfo = new ProcessStartInfo(staged) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--apply-update");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add(target + ".rollback");
        startInfo.ArgumentList.Add(updateDirectory);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动更新替换进程。");
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

public sealed record UpdateCompletionRequest(
    int OldProcessId,
    string TargetExecutable,
    string BackupExecutable,
    string UpdateDirectory,
    string StagedExecutable);

public static class UpdateCompletionService
{
    public static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 &&
        arguments[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseArguments(
        IReadOnlyList<string> arguments,
        string stagedExecutable,
        out UpdateCompletionRequest? request)
    {
        request = null;
        if (arguments.Count != 5 ||
            !IsRequested(arguments) ||
            !int.TryParse(arguments[1], out var processId) ||
            processId <= 0)
        {
            return false;
        }

        try
        {
            var staged = Path.GetFullPath(stagedExecutable);
            var target = Path.GetFullPath(arguments[2]);
            var backup = Path.GetFullPath(arguments[3]);
            var updateDirectory = Path.GetFullPath(arguments[4]);
            UpdateStorage.EnsureAllowed(updateDirectory);

            if (!File.Exists(staged) ||
                !File.Exists(target) ||
                !Path.GetFileName(target).Equals("MuseRAM.exe", StringComparison.OrdinalIgnoreCase) ||
                !PathsEqual(backup, target + ".rollback") ||
                PathsEqual(staged, target) ||
                !IsWithinDirectory(staged, updateDirectory))
            {
                return false;
            }

            request = new UpdateCompletionRequest(processId, target, backup, updateDirectory, staged);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> TryCompleteAsync(IReadOnlyList<string> arguments)
    {
        var staged = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定更新程序路径。");
        if (!TryParseArguments(arguments, staged, out var request) || request is null) return false;

        try
        {
            using var oldProcess = Process.GetProcessById(request.OldProcessId);
            await oldProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The old process has already exited.
        }

        ReplaceAndRestart(request, target =>
        {
            _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })
                ?? throw new InvalidOperationException("更新后无法重新启动 MuseRAM。");
        });
        return true;
    }

    public static void ReplaceAndRestart(UpdateCompletionRequest request, Action<string> restart)
    {
        ArgumentNullException.ThrowIfNull(restart);
        var replacement = request.TargetExecutable + ".new";
        try
        {
            File.Copy(request.StagedExecutable, replacement, true);
            File.Replace(replacement, request.TargetExecutable, request.BackupExecutable, true);
            restart(request.TargetExecutable);
        }
        catch
        {
            if (File.Exists(request.BackupExecutable))
            {
                File.Copy(request.BackupExecutable, replacement, true);
                File.Replace(replacement, request.TargetExecutable, null, true);
            }
            throw;
        }
        finally
        {
            if (File.Exists(replacement)) File.Delete(replacement);
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

public static class AppVersion
{
    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
}
