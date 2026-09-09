// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;
using Tript.Detection;

namespace Tript.App.Models;

internal sealed class GameModelManager : IDisposable
{
    internal const int SupportedModelApiVersion = ModelApiV1Compatibility.Version;
    internal const int SupportedManifestVersion = 1;

    private const long MaximumPackageBytes = 1024L * 1024 * 1024;
    private const long MaximumExtractedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private static readonly TimeSpan ManifestCheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan ManifestRetryInterval = TimeSpan.FromMinutes(15);
    private static readonly Uri DefaultManifestUri = new(
        "https://raw.githubusercontent.com/LeSqueed/Tript/main/data/model-manifest.json");
    private static readonly JsonSerializerOptions JsonOptions = new(Wire.Options)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _modelsRoot;
    private readonly string _manifestPath;
    private readonly string _manifestStatePath;
    private readonly string? _bundledManifestPath;
    private readonly Uri _manifestUri;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<string, bool>? _hasCustomModel;
    private readonly Func<string, string, CancellationToken, Task> _activate;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Version _appVersion;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _jobs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameModelStatus> _statuses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _statusGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    internal GameModelManager(
        Func<string, string, CancellationToken, Task> activate,
        string? modelsRoot = null,
        string? manifestPath = null,
        string? manifestStatePath = null,
        string? bundledManifestPath = null,
        Uri? manifestUri = null,
        HttpClient? httpClient = null,
        Func<string, bool>? hasCustomModel = null,
        Func<DateTimeOffset>? utcNow = null,
        Version? appVersion = null)
    {
        _activate = activate ?? throw new ArgumentNullException(nameof(activate));
        _modelsRoot = Path.GetFullPath(modelsRoot ?? GameModelPaths.ModelsRoot);
        _manifestPath = Path.GetFullPath(manifestPath ?? GameModelPaths.ManifestPath);
        _manifestStatePath = Path.GetFullPath(manifestStatePath ?? GameModelPaths.ManifestStatePath);
        _bundledManifestPath = bundledManifestPath;
        _manifestUri = manifestUri ?? DefaultManifestUri;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsHttp = httpClient is null;
        _hasCustomModel = hasCustomModel;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _appVersion = appVersion ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

        Directory.CreateDirectory(_modelsRoot);
        using var rootLock = GameModelInstaller.TryAcquireRootLock(_modelsRoot);
        if (rootLock is not null)
            GameModelInstaller.CleanupInterruptedInstalls(_modelsRoot);
    }

    internal event Action<IReadOnlyList<GameModelStatus>>? StatusChanged;

    internal IReadOnlyList<GameModelStatus> Snapshot()
    {
        lock (_statusGate)
            return _statuses.Values.OrderBy(status => status.GameId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal Task EnsureModelAsync(string gameId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GameModelPaths.ValidateGameId(gameId);

        var job = _jobs.GetOrAdd(gameId, id =>
            new Lazy<Task>(() => EnsureModelCoreAsync(id, _shutdown.Token),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitAndRemoveAsync(gameId, job);
    }

    private async Task AwaitAndRemoveAsync(string gameId, Lazy<Task> job)
    {
        try
        {
            await job.Value.ConfigureAwait(false);
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Lazy<Task>>>)_jobs)
                .Remove(new KeyValuePair<string, Lazy<Task>>(gameId, job));
        }
    }

    private async Task EnsureModelCoreAsync(string gameId, CancellationToken cancellationToken)
    {
        if (_hasCustomModel?.Invoke(gameId) == true)
            return;

        try
        {
            SetStatus(gameId, GameModelStage.Checking);
            var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
            var game = manifest?.Games.FirstOrDefault(entry =>
                string.Equals(entry.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            if (game is null)
            {
                ClearStatus(gameId);
                return;
            }

            var release = game.Releases
                .Where(IsCompatible)
                .OrderByDescending(candidate => candidate.Revision)
                .FirstOrDefault();
            var hasUsableModel = ModelService.HasModelForGame(gameId);
            if (release is null)
            {
                if (hasUsableModel || game.Releases.Count == 0)
                    ClearStatus(gameId);
                else
                    SetStatus(gameId, GameModelStage.Unsupported,
                        message: $"No model API {SupportedModelApiVersion} release is available.");
                return;
            }

            var installed = ReadJson<InstalledGameModel>(
                Path.Combine(_modelsRoot, gameId, "installed.json"));
            var officialHealthy = installed is not null && IsInstalledPackageHealthy(gameId, installed);
            if (installed is not null && installed.ModelApiVersion == SupportedModelApiVersion &&
                installed.Revision >= release.Revision && officialHealthy)
            {
                ClearStatus(gameId);
                return;
            }

            await DownloadValidateAndActivateAsync(gameId, release, cancellationToken).ConfigureAwait(false);
            SetStatus(gameId, GameModelStage.Ready, release.Revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearStatus(gameId);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "GameModelManager: model update failed for {GameId}", gameId);
            SetStatus(gameId, GameModelStage.Error, message: exception.Message);
        }
    }

    private bool IsCompatible(GameModelRelease release)
    {
        if (release.ModelApiVersion != SupportedModelApiVersion || release.Revision <= 0 ||
            release.SizeBytes <= 0 || release.SizeBytes > MaximumPackageBytes ||
            !Uri.TryCreate(release.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)) ||
            release.Sha256.Length != 64)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(release.MinimumAppVersion) ||
            (Version.TryParse(release.MinimumAppVersion, out var minimum) && _appVersion >= minimum);
    }

    private async Task<GameModelManifest?> GetManifestAsync(CancellationToken cancellationToken)
    {
        var cached = ReadJson<GameModelManifest>(_manifestPath) ?? ReadJson<GameModelManifest>(_bundledManifestPath);
        var cacheState = ReadJson<GameModelManifestCache>(_manifestStatePath);
        if (cached is not null && cacheState is not null &&
            _utcNow() - cacheState.CheckedAt < ManifestCheckInterval)
        {
            return ValidateManifest(cached);
        }
        if (cached is not null && cacheState is not null &&
            _utcNow() - cacheState.LastAttemptAt < ManifestRetryInterval)
        {
            return ValidateManifest(cached);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _manifestUri);
            if (!string.IsNullOrWhiteSpace(cacheState?.ETag) &&
                EntityTagHeaderValue.TryParse(cacheState.ETag, out var etag))
            {
                request.Headers.IfNoneMatch.Add(etag);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                WriteJsonAtomic(_manifestStatePath, new GameModelManifestCache
                {
                    CheckedAt = _utcNow(),
                    LastAttemptAt = _utcNow(),
                    ETag = cacheState?.ETag,
                });
                return ValidateManifest(cached);
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
                throw new InvalidDataException("The model manifest exceeded its allowed size.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var manifestBytes = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (manifestBytes.Length + read > MaximumManifestBytes)
                    throw new InvalidDataException("The model manifest exceeded its allowed size.");
                manifestBytes.Write(buffer, 0, read);
            }
            var downloaded = JsonSerializer.Deserialize<GameModelManifest>(manifestBytes.ToArray(), JsonOptions)
                ?? throw new InvalidDataException("The model manifest was empty.");
            ValidateManifest(downloaded);
            WriteJsonAtomic(_manifestPath, downloaded);
            WriteJsonAtomic(_manifestStatePath, new GameModelManifestCache
            {
                CheckedAt = _utcNow(),
                LastAttemptAt = _utcNow(),
                ETag = response.Headers.ETag?.ToString(),
            });
            return downloaded;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            if (cached is not null)
            {
                Log.Debug(exception, "GameModelManager: using cached model manifest");
                WriteJsonAtomic(_manifestStatePath, new GameModelManifestCache
                {
                    CheckedAt = cacheState?.CheckedAt ?? default,
                    LastAttemptAt = _utcNow(),
                    ETag = cacheState?.ETag,
                });
                return ValidateManifest(cached);
            }
            throw;
        }
    }

    private static GameModelManifest ValidateManifest(GameModelManifest manifest)
    {
        if (manifest.SchemaVersion != SupportedManifestVersion)
            throw new InvalidDataException($"Unsupported model manifest version {manifest.SchemaVersion}.");
        if (manifest.Games.GroupBy(game => game.GameId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("The model manifest contains duplicate game IDs.");
        foreach (var game in manifest.Games)
        {
            GameModelPaths.ValidateGameId(game.GameId);
            if (game.Releases.GroupBy(release => (release.ModelApiVersion, release.Revision)).Any(group => group.Count() > 1))
                throw new InvalidDataException($"The model manifest contains duplicate releases for {game.GameId}.");
        }
        return manifest;
    }

    private async Task DownloadValidateAndActivateAsync(string gameId, GameModelRelease release,
        CancellationToken cancellationToken)
    {
        await using var rootLock = await GameModelInstaller.AcquireRootLockAsync(_modelsRoot,
            cancellationToken).ConfigureAwait(false);
        var token = Guid.NewGuid().ToString("N");
        var downloadDirectory = Path.Combine(_modelsRoot, ".download-" + token);
        var archivePath = Path.Combine(downloadDirectory, "model.zip.part");
        var stagingPath = Path.Combine(_modelsRoot, ".install-" + token);
        Directory.CreateDirectory(downloadDirectory);

        try
        {
            SetStatus(gameId, GameModelStage.Downloading, release.Revision,
                completedBytes: 0, totalBytes: release.SizeBytes);
            using var response = await _http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.SizeBytes)
                throw new InvalidDataException("The model package size does not match the manifest.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long received = 0;
            long lastReported = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > release.SizeBytes || received > MaximumPackageBytes)
                    throw new InvalidDataException("The model package exceeded its declared size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (received - lastReported >= 256 * 1024 || received == release.SizeBytes)
                {
                    lastReported = received;
                    SetStatus(gameId, GameModelStage.Downloading, release.Revision,
                        received, release.SizeBytes);
                }
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);
            if (received != release.SizeBytes)
                throw new InvalidDataException("The downloaded model package was incomplete.");
            var archiveHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(archiveHash), Convert.FromHexString(release.Sha256)))
            {
                throw new InvalidDataException("The model package checksum did not match the manifest.");
            }

            SetStatus(gameId, GameModelStage.Verifying, release.Revision);
            Directory.CreateDirectory(stagingPath);
            ExtractPackage(archivePath, stagingPath);
            var package = ReadJson<GameModelPackageMetadata>(Path.Combine(stagingPath, "package.json"))
                ?? throw new InvalidDataException("The model package has no package.json.");
            ValidatePackageIdentity(gameId, release, package);
            ValidatePackageFiles(stagingPath, package);

            var definitions = JsonSerializer.Deserialize<List<EventDefinition>>(
                File.ReadAllText(Path.Combine(stagingPath, "events.json")), JsonOptions) ?? [];
            var hasObjectEvents = definitions.Any(definition => definition.DetectionKind == DetectionKind.Object);
            if (hasObjectEvents)
            {
                var objectModelPath = Path.Combine(stagingPath, "model.onnx");
                if (!File.Exists(objectModelPath))
                    throw new InvalidDataException("The model package has object events but no object model.");
                var metadata = OnnxModelInspector.Inspect(objectModelPath);
                var mismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
                if (mismatch is not null)
                    throw new InvalidDataException($"The model package is incompatible: {mismatch}");
            }
            var hasOcrEvents = definitions.Any(definition => definition.DetectionKind == DetectionKind.Ocr);
            if (hasOcrEvents && (!File.Exists(Path.Combine(stagingPath, "ocr_model.onnx"))
                || !File.Exists(Path.Combine(stagingPath, "ocr_dict.txt"))))
            {
                throw new InvalidDataException("The model package has OCR events but no OCR model and dictionary.");
            }

            WriteJsonAtomic(Path.Combine(stagingPath, "installed.json"), new InstalledGameModel
            {
                GameId = gameId,
                ModelApiVersion = release.ModelApiVersion,
                Revision = release.Revision,
                PackageSha256 = release.Sha256,
                ModelSha256 = package.Files.GetValueOrDefault("model.onnx")?.Sha256,
                EventsSha256 = package.Files["events.json"].Sha256,
                OcrModelSha256 = package.Files.GetValueOrDefault("ocr_model.onnx")?.Sha256,
                OcrDetectorSha256 = package.Files.GetValueOrDefault("ocr_detector.onnx")?.Sha256,
                OcrDictionarySha256 = package.Files.GetValueOrDefault("ocr_dict.txt")?.Sha256,
                InstalledAt = _utcNow(),
            });

            SetStatus(gameId, GameModelStage.Installing, release.Revision);
            await _activate(gameId, stagingPath, cancellationToken).ConfigureAwait(false);
            stagingPath = string.Empty;
        }
        finally
        {
            if (Directory.Exists(downloadDirectory))
                Directory.Delete(downloadDirectory, recursive: true);
            if (!string.IsNullOrEmpty(stagingPath) && Directory.Exists(stagingPath))
                Directory.Delete(stagingPath, recursive: true);
        }
    }

    private static void ExtractPackage(string archivePath, string stagingPath)
    {
        var required = new HashSet<string>(["events.json", "package.json"], StringComparer.Ordinal);
        var allowed = new HashSet<string>(required, StringComparer.Ordinal)
        {
            "model.onnx",
            "ocr_detector.onnx",
            "ocr_model.onnx",
            "ocr_dict.txt",
        };
        // Only the ONNX graphs may exceed the small-file cap.
        var modelGraphs = new HashSet<string>(
            ["model.onnx", "ocr_model.onnx", "ocr_detector.onnx"], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(archivePath);
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (!allowed.Contains(entry.FullName) || entry.Name != entry.FullName)
                throw new InvalidDataException($"Unexpected model package entry '{entry.FullName}'.");
            if (!seen.Add(entry.FullName))
                throw new InvalidDataException($"Duplicate model package entry '{entry.FullName}'.");
            required.Remove(entry.FullName);
            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaximumExtractedBytes ||
                (!modelGraphs.Contains(entry.Name) && entry.Length > 10 * 1024 * 1024))
            {
                throw new InvalidDataException("The model package expands beyond its allowed size.");
            }
            entry.ExtractToFile(Path.Combine(stagingPath, entry.Name));
        }
        if (required.Count > 0)
            throw new InvalidDataException($"The model package is missing {string.Join(", ", required)}.");
    }

    private static void ValidatePackageIdentity(string gameId, GameModelRelease release,
        GameModelPackageMetadata package)
    {
        if (package.PackageFormatVersion != 1 ||
            !string.Equals(package.GameId, gameId, StringComparison.OrdinalIgnoreCase) ||
            package.ModelApiVersion != release.ModelApiVersion || package.Revision != release.Revision)
        {
            throw new InvalidDataException("The model package identity does not match the manifest.");
        }
    }

    private static void ValidatePackageFiles(string stagingPath, GameModelPackageMetadata package)
    {
        var actual = Directory.EnumerateFiles(stagingPath)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name != "package.json")
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(package.Files.Keys))
            throw new InvalidDataException("The model package file metadata does not match its payload entries.");
        if (!package.Files.ContainsKey("events.json"))
            throw new InvalidDataException("The model package has no events.json metadata.");
        foreach (var name in package.Files.Keys)
        {
            if (name is not ("model.onnx" or "events.json" or "ocr_detector.onnx" or "ocr_model.onnx" or "ocr_dict.txt"))
                throw new InvalidDataException($"The model package contains unsupported metadata for {name}.");
            if (!package.Files.TryGetValue(name, out var expected) || expected.SizeBytes <= 0 ||
                expected.Sha256.Length != 64)
            {
                throw new InvalidDataException($"The model package has no valid metadata for {name}.");
            }
            var path = Path.Combine(stagingPath, name);
            var info = new FileInfo(path);
            if (info.Length != expected.SizeBytes ||
                !string.Equals(HashFile(path), expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The model package file {name} failed verification.");
            }
        }
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private bool IsInstalledPackageHealthy(string gameId, InstalledGameModel installed)
    {
        if (!string.Equals(installed.GameId, gameId, StringComparison.OrdinalIgnoreCase) ||
            installed.ModelApiVersion != SupportedModelApiVersion || installed.Revision <= 0 ||
            (installed.ModelSha256 is not null && installed.ModelSha256.Length != 64)
            || installed.EventsSha256.Length != 64)
        {
            return false;
        }

        var gamePath = Path.Combine(_modelsRoot, gameId);
        var modelPath = Path.Combine(gamePath, "model.onnx");
        var eventsPath = Path.Combine(gamePath, "events.json");
        var ocrModelPath = Path.Combine(gamePath, "ocr_model.onnx");
        var ocrDetectorPath = Path.Combine(gamePath, "ocr_detector.onnx");
        var ocrDictionaryPath = Path.Combine(gamePath, "ocr_dict.txt");
        var ocrHealthy = installed.OcrModelSha256 is null && installed.OcrDictionarySha256 is null
            || installed.OcrModelSha256 is not null && installed.OcrDictionarySha256 is not null
                && File.Exists(ocrModelPath) && File.Exists(ocrDictionaryPath)
                && string.Equals(HashFile(ocrModelPath), installed.OcrModelSha256,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(HashFile(ocrDictionaryPath), installed.OcrDictionarySha256,
                    StringComparison.OrdinalIgnoreCase);
        var ocrDetectorHealthy = installed.OcrDetectorSha256 is null
            || File.Exists(ocrDetectorPath) && string.Equals(HashFile(ocrDetectorPath), installed.OcrDetectorSha256,
                StringComparison.OrdinalIgnoreCase);
        var objectHealthy = installed.ModelSha256 is null
            || File.Exists(modelPath) && string.Equals(HashFile(modelPath), installed.ModelSha256,
                StringComparison.OrdinalIgnoreCase);
        return File.Exists(eventsPath) && objectHealthy && ocrHealthy && ocrDetectorHealthy &&
            string.Equals(HashFile(eventsPath), installed.EventsSha256, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string gameId, GameModelStage stage, int? revision = null,
        long? completedBytes = null, long? totalBytes = null, string? message = null)
    {
        IReadOnlyList<GameModelStatus> snapshot;
        lock (_statusGate)
        {
            _statuses[gameId] = new GameModelStatus
            {
                GameId = gameId,
                Stage = stage.ToString().ToLowerInvariant(),
                Revision = revision,
                CompletedBytes = completedBytes,
                TotalBytes = totalBytes,
                Message = message,
            };
            snapshot = _statuses.Values.OrderBy(status => status.GameId, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        StatusChanged?.Invoke(snapshot);
    }

    private void ClearStatus(string gameId)
    {
        IReadOnlyList<GameModelStatus>? snapshot = null;
        lock (_statusGate)
        {
            if (_statuses.Remove(gameId))
                snapshot = _statuses.Values.OrderBy(status => status.GameId, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        if (snapshot is not null)
            StatusChanged?.Invoke(snapshot);
    }

    private static T? ReadJson<T>(string? path) where T : class
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException exception)
        {
            Log.Warning(exception, "GameModelManager: ignoring invalid JSON at {Path}", path);
            return null;
        }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
        if (_ownsHttp)
            _http.Dispose();
    }
}
