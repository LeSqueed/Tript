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
using Tript.App.Resolver;
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
    internal async Task SearchGamesAsync(SearchGamesParameters? parameters, ClientHandle client)
    {
        var requestId = parameters?.RequestId?.Trim() ?? string.Empty;
        var query = parameters?.Query?.Trim() ?? string.Empty;
        if (requestId.Length == 0 || query.Length == 0)
        {
            PushGameSearchResult(client, requestId, [], "Enter a game name to search.");
            return;
        }

        if (_resolverClient is null)
        {
            PushGameSearchResult(client, requestId, [], "Game search is unavailable because no resolver URL is configured.");
            return;
        }

        try
        {
            var results = await _resolverClient.SearchAsync(query, parameters?.Limit ?? 20,
                _discoveryCancellation.Token).ConfigureAwait(false);
            PushGameSearchResult(client, requestId, results, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException
            or JsonException or TaskCanceledException)
        {
            PushGameSearchResult(client, requestId, [], "The game search service could not be reached.");
        }
    }

    private static void PushGameSearchResult(ClientHandle client, string requestId,
        IReadOnlyList<ResolverSearchResult> results, string? error)
    {
        client.Push("gameSearchResults", JsonSerializer.SerializeToElement(new
        {
            requestId,
            results,
            error,
        }, Wire.Options));
    }

    internal async Task ResolveGameSearchAsync(ResolveGameSearchParameters? parameters, ClientHandle client)
    {
        var requestId = parameters?.RequestId?.Trim() ?? string.Empty;
        var input = parameters?.Input?.Trim() ?? string.Empty;
        if (requestId.Length == 0 || input.Length == 0 || _resolverClient is null)
        {
            PushResolvedGameSearch(client, requestId, null, "The selected game could not be resolved.");
            return;
        }

        try
        {
            var game = await _resolverClient.ResolveAsync(input, _discoveryCancellation.Token)
                .ConfigureAwait(false);
            PushResolvedGameSearch(client, requestId, game, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException
            or JsonException or TaskCanceledException)
        {
            PushResolvedGameSearch(client, requestId, null, "The selected game could not be resolved.");
        }
    }

    private static void PushResolvedGameSearch(ClientHandle client, string requestId,
        ResolvedGame? game, string? error)
    {
        client.Push("gameSearchResolved", JsonSerializer.SerializeToElement(new
        {
            requestId,
            game = game is null ? null : new { game.GameId, name = game.DisplayName },
            error,
        }, Wire.Options));
    }

    internal void ReloadGameList()
    {
        var games = AppOptions.LoadCatalogue(_settingsStore.Load(), _gameCatalog, _options.GameListJson,
            out var settingsMigrated);
        if (settingsMigrated)
            _settingsStore.Save();
        AttachDiscoveredProcessPaths(games);
        lock (_gameListGate)
            _catalogueGames = games;
        EnsureModelsForGameList();
    }

    private void EnsureModelsForGameList()
    {
        var manager = _modelManager;
        if (manager is null || _disposed)
            return;

        foreach (var game in GameList)
        {
            try
            {
                _ = manager.EnsureModelAsync(game.Id);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

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

            if (_gameCatalog.EntryById(game.Id) is not null)
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

    internal List<GameInfo> GameList
    {
        get
        {
            lock (_gameListGate)
                return _catalogueGames;
        }
    }
}
