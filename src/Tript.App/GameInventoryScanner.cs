// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Core;
using Tript.GameDiscovery;
using Tript.Recorder;

namespace Tript.App;

internal sealed class GameInventoryScanner
{
    private readonly GameDiscoveryService? _discovery;
    private readonly IDiscoveryFileSystem _fileSystem;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _scanSemaphore = new(1, 1);
    private GameInventory _inventory = new([], []);
    private Task _scan = Task.CompletedTask;

    internal GameInventoryScanner(GameDiscoveryService? discovery, IDiscoveryFileSystem? fileSystem = null)
    {
        _discovery = discovery;
        _fileSystem = fileSystem ?? new PhysicalDiscoveryFileSystem();
    }

    internal GameInventory Inventory
    {
        get
        {
            lock (_gate)
                return _inventory;
        }
        set
        {
            lock (_gate)
                _inventory = value;
        }
    }

    internal Task CurrentScan => _scan;

    internal void Start(CancellationToken cancellationToken, Action onDiscovered)
    {
        if (_discovery is not null && _scan.IsCompleted && !cancellationToken.IsCancellationRequested)
            _scan = Task.Run(() => ScanAsync(_discovery, cancellationToken, onDiscovered));
    }

    internal string? FindProcessPath(GameCatalogEntry? entry, string executable)
    {
        var inventory = Inventory;
        if (inventory.Games.IsDefaultOrEmpty)
            return null;

        var normalizedExecutable = ExecutableNames.Normalize(executable);

        foreach (var installed in inventory.Games)
        {
            if (entry is not null && entry.HasStoreProduct(installed.Store, installed.ProductId.Value))
            {
                return installed.TryResolveCatalogueExecutable(
                    _fileSystem, entry.Executable, out var resolved)
                    ? ProcessNameGameDetector.NormalizePath(resolved)
                    : null;
            }

            if (entry is not null && (entry.StoreProducts is null || entry.StoreProducts.Count == 0))
            {
                if (installed.TryResolveCatalogueExecutable(
                    _fileSystem, entry.Executable, out var resolved))
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

    private async Task ScanAsync(GameDiscoveryService discovery, CancellationToken cancellationToken,
        Action onDiscovered)
    {
        try
        {
            await _scanSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            try
            {
                var inventory = await discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Inventory = inventory;
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

            if (!cancellationToken.IsCancellationRequested)
                onDiscovered();
        }
        finally
        {
            _scanSemaphore.Release();
        }
    }
}
