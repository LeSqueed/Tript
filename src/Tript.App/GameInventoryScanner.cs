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

    // Every store source records why it skipped a library or an entry (an unreadable Steam
    // libraryfolders.vdf, a missing Epic manifest directory, a registry key it could not open), and
    // nothing ever read them, so "my game isn't detected" left no trace at all.
    private static void ReportDiagnostics(GameInventory inventory)
    {
        foreach (var diagnostic in inventory.Diagnostics)
        {
            var level = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => Serilog.Events.LogEventLevel.Warning,
                DiagnosticSeverity.Warning => Serilog.Events.LogEventLevel.Information,
                _ => Serilog.Events.LogEventLevel.Debug,
            };
            Log.Write(level, "GameDiscovery: {Store} {Code}: {Message} ({Location})",
                diagnostic.Store, diagnostic.Code, diagnostic.Message, diagnostic.Location ?? "no location");
        }
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
                ReportDiagnostics(inventory);
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
