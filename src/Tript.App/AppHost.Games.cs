// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using System.Diagnostics;
using Serilog;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.App.Models;
using Tript.Core;
using Tript.Detection;
using Tript.GameDiscovery;
using Tript.Media;
using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;
#if TRIPT_TRAINING
using Tript.App.Training;
#endif
using RecorderStateMachine = Tript.Recorder.Recorder;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App;

internal sealed partial class AppHost
{
    // ---- game list ----

    internal void ReloadGameList()
    {
        var games = AppOptions.LoadCatalogue(_settingsStore.Load(), _gameCatalog, _options.GameListJson);
        AttachDiscoveredProcessPaths(games);
        lock (_gameListGate)
            _catalogueGames = games;
    }

    // The frontend sees where a packaged game is installed once the launcher inventory confirms it:
    // the exact path is what the detection targets pin, so the wire list and the targets agree.
    private void AttachDiscoveredProcessPaths(List<GameInfo> games)
    {
        GameInventory inventory;
        lock (_inventoryGate)
            inventory = _inventory;
        if (inventory.Games.IsDefaultOrEmpty)
            return;

        foreach (var game in games)
        {
            if (!game.BuiltIn || !string.IsNullOrWhiteSpace(game.ExecutablePath))
                continue;

            var discovered = DiscoveredProcessPath(game.Id, game.Executable ?? string.Empty);
            if (discovered is not null)
                game.ExecutablePath = discovered;
        }
    }

    internal bool ValidateGameList(IReadOnlyList<GameSetting> gameList, out string? failure,
        bool requireExistingExecutables = false)
    {
        failure = null;
        var packagedIds = _gameCatalog.Entries
            .Select(entry => entry.GameId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gameIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var customPaths = new HashSet<string>(FilePaths.Comparer);

        foreach (var game in gameList)
        {
            if (string.IsNullOrWhiteSpace(game.Id))
            {
                failure = "a game is missing its identity.";
                return false;
            }

            if (!gameIds.Add(game.Id))
            {
                failure = $"two games share the identity '{game.Id}'.";
                return false;
            }

            if (packagedIds.Contains(game.Id))
            {
                if (!string.IsNullOrWhiteSpace(game.ExecutablePath))
                {
                    failure = $"'{game.Id}' is a packaged game; its executable identity cannot be changed.";
                    return false;
                }

                continue;
            }

            try
            {
                GameModelPaths.ValidateGameId(game.Id);
            }
            catch (ArgumentException)
            {
                failure = $"'{game.Id}' is not a safe game identity.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(game.Name))
            {
                failure = "a custom game is missing its name.";
                return false;
            }

            var path = game.ExecutablePath?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !FilePaths.IsFullyQualified(path))
            {
                failure = $"'{game.Id}' needs an exact absolute executable path.";
                return false;
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException
                or NotSupportedException or PathTooLongException)
            {
                failure = $"'{game.Id}' has an unusable executable path.";
                return false;
            }

            if (!customPaths.Add(normalizedPath))
            {
                failure = $"two custom games use the same executable: '{path}'.";
                return false;
            }

            if (requireExistingExecutables && !File.Exists(normalizedPath))
            {
                failure = $"'{game.Id}' points to an executable that does not exist.";
                return false;
            }

            if (OperatingSystem.IsWindows() && !ExecutableNames.HasExeExtension(normalizedPath))
            {
                failure = $"'{game.Id}' must point to an .exe.";
                return false;
            }
        }

        return true;
    }

    // Reject invalid effective windows instead of silently relying on resolver clamping.
    // SettingsModel avoids the Tript.Settings namespace collision in this file.
    internal static bool ValidateAutomaticClipWindows(SettingsModel settings, out string? failure)
    {
        failure = null;

        var globalBefore = settings.Recording.AutomaticClipBeforeSeconds;
        var globalAfter = settings.Recording.AutomaticClipAfterSeconds;
        if (globalAfter < globalBefore)
        {
            failure = "seconds after a bookmark cannot be lower than seconds before.";
            return false;
        }

        foreach (var game in settings.Game.GameList)
        {
            var effectiveBefore = game.AutomaticClipOverride?.BeforeSeconds ?? globalBefore;
            var effectiveAfter = game.AutomaticClipOverride?.AfterSeconds ?? globalAfter;
            if (effectiveAfter < effectiveBefore)
            {
                failure = $"'{game.Name}': seconds after a bookmark cannot be lower than seconds before.";
                return false;
            }
        }

        return true;
    }

    // A plain read. The getter used to reload whenever the catalogue was empty, which made a user who
    // deleted every game entry re-read the settings file on every access — from every thread, while
    // clearing and refilling the one list the other threads were enumerating. The catalogue is loaded
    // at startup and reloaded when the settings that define it change, which is the only time it can
    // differ.
    internal List<GameInfo> GameList
    {
        get
        {
            lock (_gameListGate)
                return _catalogueGames;
        }
    }
}
