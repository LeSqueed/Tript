// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Core;
using Tript.GameDiscovery;
using Tript.Recorder;

namespace Tript.App;

internal sealed partial class AppHost
{
    private void StartDiscoveryScan()
    {
        if (_detector is not null && _discovery is not null && _discoveryTask.IsCompleted
            && !_discoveryCancellation.IsCancellationRequested)
            _discoveryTask = Task.Run(() => ScanDiscoveryAsync(_discoveryCancellation.Token));
    }

    private List<GameDetectionTarget> BuildDetectionTargets()
    {
        var targets = new List<GameDetectionTarget>();
        foreach (var game in GameList)
        {
            if (string.IsNullOrWhiteSpace(game.Executable))
                continue;

            if (!game.BuiltIn)
            {
                var customPath = NormalizePickedExecutable(game.ExecutablePath);
                if (customPath is not null)
                    targets.Add(new GameDetectionTarget(game.Id, game.Executable,
                        ProcessNameGameDetector.NormalizePath(customPath)));
                continue;
            }

            var discoveredPath = DiscoveredProcessPath(game.Id, game.Executable);
            targets.Add(discoveredPath is null
                ? new GameDetectionTarget(game.Id, game.Executable)
                : new GameDetectionTarget(game.Id, game.Executable, discoveredPath));
        }

        return targets;
    }

    private string? DiscoveredProcessPath(string gameId, string executable)
    {
        GameInventory inventory;
        lock (_inventoryGate)
            inventory = _inventory;
        if (inventory.Games.IsDefaultOrEmpty)
            return null;

        var entry = _gameCatalog.EntryById(gameId);
        var normalizedExecutable = ExecutableNames.Normalize(executable);

        foreach (var installed in inventory.Games)
        {
            if (entry is not null && entry.HasStoreProduct(installed.Store, installed.ProductId.Value))
            {
                return installed.TryResolveCatalogueExecutable(
                    _discoveryFileSystem, entry.Executable, out var resolved)
                    ? ProcessNameGameDetector.NormalizePath(resolved)
                    : null;
            }

            if (entry is not null && (entry.StoreProducts is null || entry.StoreProducts.Count == 0))
            {
                if (installed.TryResolveCatalogueExecutable(
                    _discoveryFileSystem, entry.Executable, out var resolved))
                {
                    return ProcessNameGameDetector.NormalizePath(resolved);
                }

                if (MatchingExecutablePath(installed, normalizedExecutable) is { } matched)
                    return matched;
            }
        }

        return null;
    }

    private static string? MatchingExecutablePath(InstalledGame installed, string normalizedExecutable)
    {
        foreach (var path in installed.ExecutablePaths)
        {
            if (path.Length > 0 && ExecutableNames.Comparer.Equals(ExecutableNames.Normalize(path), normalizedExecutable))
                return ProcessNameGameDetector.NormalizePath(path);
        }

        return null;
    }

    private async Task ScanDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_discovery is null)
            return;

        try
        {
            await _discoveryScanSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            try
            {
                var inventory = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_inventoryGate)
                    _inventory = inventory;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.Warning(exception,
                    "AppHost: launcher game discovery failed; detection continues on the packaged catalogue and custom games.");
                return;
            }

            if (_disposed || cancellationToken.IsCancellationRequested)
                return;

            var previousPaths = GameList.Select(game => (game.Id, game.ExecutablePath)).ToList();
            ReloadGameList();
            var discoveredPaths = GameList.Select(game => (game.Id, game.ExecutablePath)).ToList();
            if (!previousPaths.SequenceEqual(discoveredPaths))
            {
                RebuildDetectionTargets();
                PushGameList();
            }
        }
        finally
        {
            _discoveryScanSemaphore.Release();
        }
    }

    private void RebuildDetectionTargets()
    {
        var targets = BuildDetectionTargets();
        _detector?.UpdateTargets(targets);
        _fullscreenDetector?.UpdateKnownTargets(targets);
    }

    internal void SetInventoryForTesting(GameInventory inventory)
    {
        lock (_inventoryGate)
            _inventory = inventory;
    }
}
