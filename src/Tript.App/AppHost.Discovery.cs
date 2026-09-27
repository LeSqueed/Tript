// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.GameDiscovery;
using Tript.Recorder;

namespace Tript.App;

internal sealed partial class AppHost
{
    private static GameDiscoveryService? CreateGameDiscovery()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            return GameDiscoveryService.CreateDefault(new WindowsXboxPackageProvider());
        if (OperatingSystem.IsLinux())
            return GameDiscoveryService.CreateLinuxDefault();
        return null;
    }

    private void StartDiscoveryScan()
    {
        if (_detector is not null)
            _gameInventory.Start(_discoveryCancellation.Token, OnInventoryDiscovered);
    }

    internal List<GameDetectionTarget> BuildDetectionTargets()
    {
        var targets = new List<GameDetectionTarget>();
        foreach (var game in GameList)
        {
            if (string.IsNullOrWhiteSpace(game.Executable))
                continue;

            var path = NormalizePickedExecutable(game.ExecutablePath);
            if (path is not null)
                targets.Add(new GameDetectionTarget(game.Id, game.Executable,
                    ProcessNameGameDetector.NormalizePath(path)));
            else if (string.IsNullOrWhiteSpace(game.ExecutablePath) && !IsCustomGameId(game.Id))
                targets.Add(new GameDetectionTarget(game.Id, game.Executable));
        }

        return targets;
    }

    private void OnInventoryDiscovered()
    {
        if (!_disposed)
            _ = ReconcileCustomGameIdentitiesAsync();
    }

    private void RebuildDetectionTargets()
    {
        var targets = BuildDetectionTargets();
        _detector?.UpdateTargets(targets);
        _fullscreenDetector?.UpdateKnownTargets(targets);
    }

    private IReadOnlyList<string> InstalledGameRoots() =>
        _gameInventory.Inventory.Games.Select(game => game.InstallRoot).ToList();

        internal void SetInventoryForTesting(GameInventory inventory) => _gameInventory.Inventory = inventory;
}
