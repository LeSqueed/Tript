// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;

namespace Tript.App.Updater;

// Mirrors Models/GameModelManager's shape (injectable HttpClient, streaming download with
// incremental SHA-256, staged-then-atomic install) but with a much smaller surface: there is only
// ever one artifact (App\), Tript already enforces single-instance, and the only writer of App\ is
// launcher.c at a point where no .NET process is running - so no cross-process lock file is
// needed, just an in-process non-blocking gate to stop the manual button and the timer racing.
internal sealed class UpdateManager : IDisposable
{
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExtractedBytes = 4L * 1024 * 1024 * 1024;

    private readonly string _installRoot;
    private readonly string _currentVersion;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly bool _supportsAutomaticApply;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _statusGate = new();
    private UpdateStatusPayload _status = new() { Stage = "idle" };
    private bool _disposed;

    internal UpdateManager(string installRoot, string currentVersion, HttpClient? httpClient = null,
        GitHubReleaseClient? releaseClient = null, bool? supportsAutomaticApply = null)
    {
        _installRoot = installRoot;
        _currentVersion = currentVersion;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsHttp = httpClient is null;
        _releaseClient = releaseClient ?? new GitHubReleaseClient(_http);
        // Windows-only in production (the whole download/stage/apply pipeline assumes launcher.c's
        // swap contract), but injectable so tests can exercise both branches deterministically
        // regardless of which OS actually runs them (CI runs this suite on Linux).
        _supportsAutomaticApply = supportsAutomaticApply ?? OperatingSystem.IsWindows();
    }

    internal event Action<UpdateStatusPayload>? StatusChanged;

    // Reads the on-disk marker fresh on every call - this, plus AppController pushing this
    // snapshot on every new IPC connection, is the entire "reappear on relaunch" mechanism. No
    // "dismissed" flag is ever persisted anywhere.
    internal UpdateStatusPayload Snapshot()
    {
        var marker = UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot));
        if (marker is not null && StagedShellExists(marker))
            return new UpdateStatusPayload { Stage = "ready", Version = marker.Version };

        lock (_statusGate)
            return _status;
    }

    internal async Task CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _checkGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            SetStatus(UpdateStage.Checking);
            var release = await _releaseClient.GetLatestPublishedReleaseAsync(cancellationToken)
                .ConfigureAwait(false);
            if (release is null)
            {
                if (manual)
                    SetStatus(UpdateStage.Error, error: "Could not reach GitHub to check for updates.");
                else
                    Log.Debug("UpdateManager: automatic update check found nothing to report");
                return;
            }

            if (!ReleaseVersion.TryParse(release.TagName, out var candidateVersion)
                || !ReleaseVersion.IsNewer(release.TagName, _currentVersion))
            {
                SetStatus(UpdateStage.UpToDate);
                return;
            }

            var versionText = candidateVersion.ToString();

            var existingMarker = UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot));
            if (existingMarker is not null
                && string.Equals(existingMarker.Version, versionText, StringComparison.Ordinal)
                && StagedShellExists(existingMarker))
            {
                // Already downloaded and staged this exact version on a previous check.
                SetStatus(UpdateStage.Ready, version: versionText);
                return;
            }

            if (!_supportsAutomaticApply)
            {
                SetStatus(UpdateStage.Available, version: versionText, releaseUrl: release.HtmlUrl);
                return;
            }

            var assetNames = GitHubReleaseClient.ParseTagAndAssetNames(release);
            if (assetNames is null)
            {
                SetStatus(UpdateStage.Available, version: versionText, releaseUrl: release.HtmlUrl);
                return;
            }

            var zipAsset = release.Assets.First(asset =>
                string.Equals(asset.Name, assetNames.Value.ZipAssetName, StringComparison.Ordinal));
            var shaAsset = release.Assets.First(asset =>
                string.Equals(asset.Name, assetNames.Value.Sha256AssetName, StringComparison.Ordinal));

            await DownloadVerifyAndStageAsync(versionText, release.HtmlUrl, zipAsset, shaAsset, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or InvalidDataException or JsonException or UnauthorizedAccessException
            or TaskCanceledException)
        {
            if (manual)
                SetStatus(UpdateStage.Error, error: exception.Message);
            else
                Log.Debug(exception, "UpdateManager: automatic update check failed");
        }
        finally
        {
            _checkGate.Release();
        }
    }

    // Re-reads the marker fresh from disk, not cached state - the primary safeguard against ever
    // firing a restart-for-update with nothing genuinely staged.
    internal bool TryApply()
    {
        var marker = UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot));
        return marker is not null && StagedShellExists(marker);
    }

    private bool StagedShellExists(UpdateMarker marker) => File.Exists(Path.Combine(
        UpdateStagingPaths.StagedFolderPath(_installRoot, marker.StagedFolderName), "Tript.Shell.exe"));

    // Called once at AppHost construction: clears the previous swap's backup, drops the marker
    // once the running app has actually reached the version it named, and removes any staged
    // folder that isn't (or is no longer) referenced by a live marker.
    internal void SweepLeftovers()
    {
        var stagingRoot = UpdateStagingPaths.StagingRoot(_installRoot);
        if (!Directory.Exists(stagingRoot))
            return;

        var markerPath = UpdateStagingPaths.MarkerPath(_installRoot);
        var marker = UpdateMarker.TryRead(markerPath);
        if (marker is not null && ReleaseVersion.TryParse(marker.Version, out var markerVersion)
            && ReleaseVersion.TryParse(_currentVersion, out var currentVersion)
            && markerVersion.CompareTo(currentVersion) <= 0)
        {
            DeleteFileIfExists(markerPath);
            marker = null;
        }

        DeleteDirectoryIfExists(UpdateStagingPaths.OldAppBackupPath(_installRoot));

        foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "old-App", StringComparison.Ordinal))
                continue;
            if (marker is not null && string.Equals(name, marker.StagedFolderName, StringComparison.Ordinal))
                continue;

            DeleteDirectoryIfExists(directory);
        }

        foreach (var file in Directory.EnumerateFiles(stagingRoot, "download-*.zip.part"))
            DeleteFileIfExists(file);

        DeleteStagingRootIfEmpty();
    }

    internal static string? CurrentInstalledVersion()
    {
        var informational = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return null;

        var plusIndex = informational.IndexOf('+', StringComparison.Ordinal);
        return plusIndex < 0 ? informational : informational[..plusIndex];
    }

    private async Task DownloadVerifyAndStageAsync(string version, string releaseUrl, GitHubReleaseAsset zipAsset,
        GitHubReleaseAsset shaAsset, CancellationToken cancellationToken)
    {
        var stagingRoot = UpdateStagingPaths.StagingRoot(_installRoot);
        Directory.CreateDirectory(stagingRoot);
        var token = Guid.NewGuid().ToString("N");
        var downloadPath = UpdateStagingPaths.DownloadPath(_installRoot, token);
        var stagedFolderName = UpdateStagingPaths.NewStagedFolderName(token);
        var stagedPath = UpdateStagingPaths.StagedFolderPath(_installRoot, stagedFolderName);

        try
        {
            SetStatus(UpdateStage.Downloading, version: version, releaseUrl: releaseUrl, completedBytes: 0,
                totalBytes: zipAsset.Size);

            var sidecarContent = await DownloadTextAsync(shaAsset.BrowserDownloadUrl, cancellationToken)
                .ConfigureAwait(false);
            var expectedHex = Sha256Sidecar.TryParse(sidecarContent)
                ?? throw new InvalidDataException("The update checksum file could not be read.");

            var actualHex = await DownloadZipWithProgressAsync(zipAsset, downloadPath, version, releaseUrl,
                cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHex), Convert.FromHexString(expectedHex)))
            {
                throw new InvalidDataException("The downloaded update's checksum did not match.");
            }

            Directory.CreateDirectory(stagedPath);
            try
            {
                ExtractAppSubtree(downloadPath, stagedPath);
                if (!File.Exists(Path.Combine(stagedPath, "Tript.Shell.exe"))
                    || !Directory.Exists(Path.Combine(stagedPath, "dist")))
                {
                    throw new InvalidDataException("The downloaded update package looked incomplete.");
                }

                UpdateMarker.WriteAtomic(UpdateStagingPaths.MarkerPath(_installRoot),
                    new UpdateMarker(UpdateMarker.CurrentFormatVersion, version, stagedFolderName));
            }
            catch
            {
                DeleteDirectoryIfExists(stagedPath);
                throw;
            }

            SetStatus(UpdateStage.Ready, version: version, releaseUrl: releaseUrl);
        }
        finally
        {
            DeleteFileIfExists(downloadPath);
            DeleteStagingRootIfEmpty();
        }
    }

    private async Task<string> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadZipWithProgressAsync(GitHubReleaseAsset asset, string downloadPath,
        string version, string releaseUrl, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != asset.Size)
            throw new InvalidDataException("The update package size does not match the release.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long received = 0;
        long lastReported = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            received += read;
            if (received > asset.Size || received > MaximumPackageBytes)
                throw new InvalidDataException("The update package exceeded its declared size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (received - lastReported >= 256 * 1024 || received == asset.Size)
            {
                lastReported = received;
                SetStatus(UpdateStage.Downloading, version: version, releaseUrl: releaseUrl,
                    completedBytes: received, totalBytes: asset.Size);
            }
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await output.DisposeAsync().ConfigureAwait(false);
        if (received != asset.Size)
            throw new InvalidDataException("The downloaded update package was incomplete.");

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // Extracts only the "<top>/App/**" subtree of the zip (the launcher, "<top>/Tript.exe", is
    // never replaced - see the plan's decision to extend launcher.c instead of swapping it out).
    // Zip-slip-safe: every entry's stripped-and-resolved path is checked to stay under stagingPath.
    private static void ExtractAppSubtree(string archivePath, string stagingPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        string? topLevel = null;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            var slash = normalized.IndexOf('/');
            if (slash <= 0)
                throw new InvalidDataException(
                    "The update package does not have the expected single top-level folder.");
            var candidateTop = normalized[..slash];
            topLevel ??= candidateTop;
            if (!string.Equals(topLevel, candidateTop, StringComparison.Ordinal))
                throw new InvalidDataException("The update package has more than one top-level folder.");
        }
        if (topLevel is null)
            throw new InvalidDataException("The update package is empty.");

        var appPrefix = $"{topLevel}/App/";
        var fullStagingPath = Path.GetFullPath(stagingPath);
        var safeRoot = fullStagingPath + Path.DirectorySeparatorChar;
        var sawAppEntry = false;
        long extractedBytes = 0;

        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith(appPrefix, StringComparison.Ordinal))
                continue;

            var isDirectoryEntry = normalized.EndsWith('/');
            var relative = normalized[appPrefix.Length..];
            if (isDirectoryEntry && relative.Length > 0)
                relative = relative[..^1];
            if (relative.Length == 0)
                continue; // the App/ directory entry itself (real zip tools emit one explicitly).

            var segments = relative.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
                throw new InvalidDataException($"The update package contains an unsafe entry '{entry.FullName}'.");

            var destination = Path.GetFullPath(Path.Combine(fullStagingPath, relative));
            if (!destination.StartsWith(safeRoot, StringComparison.Ordinal))
                throw new InvalidDataException($"The update package contains an unsafe entry '{entry.FullName}'.");

            sawAppEntry = true;
            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaximumExtractedBytes)
                throw new InvalidDataException("The update package expands beyond its allowed size.");

            if (isDirectoryEntry)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        if (!sawAppEntry)
            throw new InvalidDataException("The update package has no App folder.");
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Without this, .tript-update\ lingers forever as an empty directory once whatever put
    // something in it (a failed check, or a completed-and-since-applied update) is done - nothing
    // else ever removes the staging root itself, only its contents.
    private void DeleteStagingRootIfEmpty()
    {
        var stagingRoot = UpdateStagingPaths.StagingRoot(_installRoot);
        try
        {
            if (Directory.Exists(stagingRoot) && !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
                Directory.Delete(stagingRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SetStatus(UpdateStage stage, string? version = null, string? releaseUrl = null,
        long? completedBytes = null, long? totalBytes = null, string? error = null)
    {
        var payload = new UpdateStatusPayload
        {
            Stage = StageToWireString(stage),
            Version = version,
            ReleaseUrl = releaseUrl,
            CompletedBytes = completedBytes,
            TotalBytes = totalBytes,
            Error = error,
        };
        lock (_statusGate)
            _status = payload;
        StatusChanged?.Invoke(payload);
    }

    private static string StageToWireString(UpdateStage stage) => stage switch
    {
        UpdateStage.Idle => "idle",
        UpdateStage.Checking => "checking",
        UpdateStage.UpToDate => "upToDate",
        UpdateStage.Available => "available",
        UpdateStage.Downloading => "downloading",
        UpdateStage.Ready => "ready",
        UpdateStage.Error => "error",
        _ => "idle",
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _checkGate.Dispose();
        if (_ownsHttp)
            _http.Dispose();
    }
}
