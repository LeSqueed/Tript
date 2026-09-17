// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using System.Reflection;
using Serilog;
using Tript.Detection;

namespace Tript.App.Models;

internal sealed class GameModelManager : IDisposable
{
    internal const int SupportedModelApiVersion = ModelApiV1Compatibility.Version;

    private readonly string _modelsRoot;
    private readonly GameModelManifestSource _manifest;
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
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsHttp = httpClient is null;
        _hasCustomModel = hasCustomModel;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _manifest = new GameModelManifestSource(
            Path.GetFullPath(manifestPath ?? GameModelPaths.ManifestPath),
            Path.GetFullPath(manifestStatePath ?? GameModelPaths.ManifestStatePath),
            bundledManifestPath, manifestUri, _http, _utcNow);
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
            var manifest = await _manifest.GetAsync(cancellationToken).ConfigureAwait(false);
            var game = manifest?.Games.FirstOrDefault(entry =>
                string.Equals(entry.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            if (game is null)
            {
                SetStatus(gameId, GameModelStage.Unsupported,
                    message: "No model has been published for this game yet.");
                return;
            }

            var release = game.Releases
                .Where(candidate => GameModelPackage.IsCompatible(candidate, _appVersion))
                .OrderByDescending(candidate => candidate.Revision)
                .FirstOrDefault();
            var hasUsableModel = ModelService.HasModelForGame(gameId);
            if (release is null)
            {
                if (hasUsableModel)
                    ClearStatus(gameId);
                else if (game.Releases.Count == 0)
                    SetStatus(gameId, GameModelStage.Unsupported,
                        message: "No model has been published for this game yet.");
                else
                    SetStatus(gameId, GameModelStage.Unsupported,
                        message: $"No model API {SupportedModelApiVersion} release is available.");
                return;
            }

            var installed = ModelJsonFiles.Read<InstalledGameModel>(
                Path.Combine(_modelsRoot, gameId, "installed.json"));
            var officialHealthy = installed is not null && GameModelPackage.IsInstalledHealthy(_modelsRoot, gameId, installed);
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
            await GameModelPackage.DownloadAsync(_http, release, archivePath,
                received => SetStatus(gameId, GameModelStage.Downloading, release.Revision,
                    received, release.SizeBytes),
                cancellationToken).ConfigureAwait(false);

            SetStatus(gameId, GameModelStage.Verifying, release.Revision);
            var package = GameModelPackage.ExtractAndVerify(archivePath, stagingPath, gameId, release);
            ModelJsonFiles.WriteAtomic(Path.Combine(stagingPath, "installed.json"),
                GameModelPackage.InstalledRecord(gameId, release, package, _utcNow()));

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

    internal void PruneStatuses(IReadOnlyCollection<string> currentGameIds)
    {
        var keep = new HashSet<string>(currentGameIds, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<GameModelStatus>? snapshot = null;
        lock (_statusGate)
        {
            var stale = _statuses.Keys.Where(gameId => !keep.Contains(gameId)).ToArray();
            if (stale.Length > 0)
            {
                foreach (var gameId in stale)
                    _statuses.Remove(gameId);
                snapshot = _statuses.Values.OrderBy(status => status.GameId, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        if (snapshot is not null)
            StatusChanged?.Invoke(snapshot);
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
