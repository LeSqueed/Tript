// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;

namespace Tript.App.Updater;

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
        _supportsAutomaticApply = supportsAutomaticApply ?? OperatingSystem.IsWindows();
    }

    internal event Action<UpdateStatusPayload>? StatusChanged;

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

            // A rolled-back release would otherwise be downloaded and staged again the next day, and
            // fail again. An automatic check leaves it; a manual one is a deliberate retry. Any newer
            // release makes the record obsolete.
            var rolledBack = RolledBackVersion();
            if (rolledBack is not null)
            {
                if (!string.Equals(rolledBack, versionText, StringComparison.Ordinal))
                {
                    DeleteFileIfExists(UpdateStagingPaths.RolledBackPath(_installRoot));
                }
                else if (!manual)
                {
                    Log.Information("UpdateManager: {Version} was rolled back after it failed to start; "
                        + "not applying it again automatically", versionText);
                    SetStatus(UpdateStage.Available, version: versionText, releaseUrl: release.HtmlUrl);
                    return;
                }
            }

            var existingMarker = UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot));
            if (existingMarker is not null
                && string.Equals(existingMarker.Version, versionText, StringComparison.Ordinal)
                && StagedShellExists(existingMarker))
            {
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
            // A manual failure only reached the UI, and an automatic one only Debug, so a rejected
            // update left no durable record. An integrity failure (checksum mismatch, unsafe zip entry,
            // a size that disagrees with the release) means a corrupted or tampered download and must
            // be visible either way. Being offline during an automatic check is routine.
            if (exception is InvalidDataException)
                Log.Warning(exception, "UpdateManager: the update was rejected as invalid");
            else if (manual)
                Log.Warning(exception, "UpdateManager: the update check failed");
            else
                Log.Debug(exception, "UpdateManager: automatic update check failed");

            if (manual)
                SetStatus(UpdateStage.Error, error: exception.Message);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    // Must stay longer than TRIPT_UPDATE_PROBATION_MS in launcher.c: until the launcher's window has
    // passed, old-App is what it rolls back to.
    internal static readonly TimeSpan PreviousInstallProbation = TimeSpan.FromMinutes(2);

    internal string? RolledBackVersion()
    {
        try
        {
            var path = UpdateStagingPaths.RolledBackPath(_installRoot);
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Called once this version has run past the probation window, so the launcher can no longer
    // roll it back and the previous install is no longer needed.
    internal void DiscardPreviousInstall()
    {
        if (_disposed)
            return;

        DeleteDirectoryIfExists(UpdateStagingPaths.OldAppBackupPath(_installRoot));
        DeleteStagingRootIfEmpty();
    }

    // Read fresh from disk so a restart is never requested with nothing staged.
    internal bool TryApply()
    {
        var marker = UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot));
        return marker is not null && StagedShellExists(marker);
    }

    private bool StagedShellExists(UpdateMarker marker) => File.Exists(Path.Combine(
        UpdateStagingPaths.StagedFolderPath(_installRoot, marker.StagedFolderName), "Tript.Shell.exe"));

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

        // old-App is deliberately kept here. It is the only way back if this version turns out not
        // to start, and deleting it on the first startup is what used to make a bad release
        // unrecoverable. DiscardPreviousInstall removes it once this version has run past the
        // launcher's probation window.

        // After a rollback the marker still names the version that failed, and its staged folder is
        // gone (it became App, then failed-App). Left in place, the UI would offer "Restart & update"
        // for the very build that could not start.
        if (marker is not null && string.Equals(marker.Version, RolledBackVersion(), StringComparison.Ordinal))
        {
            DeleteFileIfExists(markerPath);
            marker = null;
        }

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

    // Every entry is checked to stay under stagingPath (zip slip).
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
                continue;

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
            Log.Warning(exception, "UpdateManager: could not remove {Path}", path);
        }
    }

    // Not just tidying: a leftover old-App makes the launcher's first MoveFileW fail, so every later
    // update is silently never applied. A failure here is the only trace of that.
    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning(exception, "UpdateManager: could not remove {Path}; a later update may not apply", path);
        }
    }

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
