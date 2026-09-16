// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    internal async Task RequestGameAddAsync(RequestGameAddParameters? parameters, ClientHandle client)
    {
        var requestId = parameters?.RequestId?.Trim() ?? string.Empty;
        var gameId = parameters?.GameId?.Trim() ?? string.Empty;
        if (requestId.Length == 0 || gameId.Length == 0 || _resolverClient is null)
        {
            PushGameAddRequested(client, requestId, gameId, "rejected", null,
                "The request could not be sent.");
            return;
        }

        try
        {
            var result = await _resolverClient.RequestGameAsync(gameId, InstallIdentity.Value,
                _discoveryCancellation.Token).ConfigureAwait(false);
            var status = result.Status switch
            {
                GameRequestStatus.Accepted => "accepted",
                GameRequestStatus.AlreadyRequested => "alreadyRequested",
                _ => "rateLimited",
            };
            PushGameAddRequested(client, requestId, gameId, status,
                (int?)result.RetryAfter?.TotalSeconds, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException
            or JsonException or TaskCanceledException)
        {
            PushGameAddRequested(client, requestId, gameId, "rejected", null,
                "The request could not be sent.");
        }
    }

    private static void PushGameAddRequested(ClientHandle client, string requestId, string gameId,
        string status, int? retryAfterSeconds, string? error)
    {
        client.Push("gameAddRequested", JsonSerializer.SerializeToElement(new
        {
            requestId,
            gameId,
            status,
            retryAfterSeconds,
            error,
        }, Wire.Options));
    }

    internal void ReloadGameList()
    {
        var games = AppOptions.LoadCatalogue(_settingsStore.Load(), _gameCatalog, _options.GameListJson,
            out var settingsMigrated, _gameIdAliases);
        if (settingsMigrated)
            _settingsStore.Save();
        AttachDiscoveredProcessPaths(games);
        lock (_gameListGate)
        {
            _catalogueGames = games;
            _libraryGames = new LibraryGames(games, _gameIdAliases);
        }
        EnsureModelsForGameList();
    }

    private void EnsureModelsForGameList()
    {
        var manager = _modelManager;
        if (manager is null || _disposed)
            return;

        var games = GameList;
        manager.PruneStatuses(games.Select(game => game.Id).ToArray());
        foreach (var game in games)
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

    // Retries resolver identification for locally-minted "custom-" games (created when the
    // resolver was unreachable at detection time, or added by hand). If the resolver now returns a
    // different, canonical id, the game entry is migrated in place — name, executable path, and
    // every override survive since only the Id field changes — and the old id is recorded as an
    // alias so already-recorded sessions/clips keep resolving to the same (now renamed) entry
    // without any historical file being rewritten. See GameIdAliasStore / ResolveStoredGameId.
    internal async Task ReconcileCustomGameIdentitiesAsync()
    {
        if (_resolverClient is null || _disposed)
            return;

        var candidates = GameList
            .Where(game => game.Id.StartsWith("custom-", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(game.ExecutablePath))
            .ToArray();
        if (candidates.Length == 0)
            return;

        GameInventory inventory;
        lock (_inventoryGate)
            inventory = _inventory;

        var migrated = false;
        foreach (var game in candidates)
        {
            if (_disposed || _shuttingDown)
                return;

            var resolution = CandidateResolverInput(game.Executable ?? string.Empty, game.ExecutablePath!, inventory);
            if (!resolution.StoreBacked)
                continue;

            ResolvedGame resolved;
            try
            {
                resolved = await _resolverClient.ResolveAsync(resolution.Input, _discoveryCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_discoveryCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                or OperationCanceledException)
            {
                Log.Debug(exception, "AppHost: identity recheck failed for {GameId}", game.Id);
                continue;
            }

            if (string.Equals(resolved.GameId, game.Id, StringComparison.OrdinalIgnoreCase))
                continue;

            var oldId = game.Id;
            var didMutate = false;
            bool saved;
            lock (_settingsUpdateGate)
            {
                saved = _settingsStore.TryUpdate(settings =>
                {
                    var entry = settings.Game.GameList.FirstOrDefault(value =>
                        string.Equals(value.Id, oldId, StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                        return null; // removed or edited concurrently; nothing left to migrate

                    var existingCanonical = settings.Game.GameList.FirstOrDefault(value =>
                        !ReferenceEquals(value, entry)
                        && string.Equals(value.Id, resolved.GameId, StringComparison.OrdinalIgnoreCase));
                    if (existingCanonical is not null)
                        settings.Game.GameList.Remove(entry);
                    else
                        entry.Id = resolved.GameId;

                    didMutate = true;
                    return ValidateGameList(settings.Game.GameList, out var validationError)
                        ? null
                        : validationError;
                }, out _, out var failure);

                if (!saved)
                    Log.Warning("AppHost: identity migration for {OldId} was not saved: {Reason}", oldId, failure);
            }

            if (!saved || !didMutate)
                continue;

            _gameIdAliases.TryAdd(oldId, resolved.GameId);
            migrated = true;
        }

        if (migrated)
        {
            ReloadGameList();
            RebuildDetectionTargets();
            PushGameList();
            PushSettings();
            PushModelStatus();
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

    private LibraryGames Games
    {
        get
        {
            lock (_gameListGate)
                return _libraryGames ??= new LibraryGames(_catalogueGames, _gameIdAliases);
        }
    }

    [return: NotNullIfNotNull(nameof(gameId))]
    internal string? GameDisplayName(string? gameId) => Games.Find(gameId)?.Name ?? gameId;
}
