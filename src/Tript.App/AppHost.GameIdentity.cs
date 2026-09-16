// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;

namespace Tript.App;

internal sealed partial class AppHost
{
    internal string ResolveDetectedGameId(string processName)
    {
        foreach (var game in GameList)
        {
            if (game.Id.Length == 0)
                continue;

            if (ExecutableNames.Comparer.Equals(ExecutableNames.Normalize(LibraryGames.ExecutableOf(game)), processName))
                return game.Id;
        }

        return processName;
    }

    private string? ResolveStoredGameId(string? storedId, string? gameName) =>
        Games.ResolveStoredGameId(storedId, gameName);

    private (string? Game, string? GameId) ResolveGameForSession(string sourceSessionPath)
    {
        var sessionFile = Path.GetFileName(sourceSessionPath);
        if (!string.IsNullOrWhiteSpace(sessionFile))
        {
            var metadata = _metadata.Load(sessionFile);
            if (metadata is not null)
            {
                var game = string.IsNullOrWhiteSpace(metadata.Game) ? null : metadata.Game;
                var gameId = ResolveStoredGameId(metadata.GameId, game);
                if (game is not null || gameId is not null)
                    return (game, gameId);
            }
        }

        if (!string.IsNullOrWhiteSpace(_activeSessionPath)
            && string.Equals(Path.GetFileName(_activeSessionPath), sessionFile,
                StringComparison.OrdinalIgnoreCase)
            && _pendingMetadata is not null)
        {
            var pendingGame = string.IsNullOrWhiteSpace(_pendingMetadata.Game) ? null : _pendingMetadata.Game;
            var pendingGameId = ResolveStoredGameId(_pendingMetadata.GameId, pendingGame);
            if (pendingGame is not null || pendingGameId is not null)
                return (pendingGame, pendingGameId);
        }

        return (null, null);
    }

    private void AttachGameToClips(IEnumerable<string> clipFiles, string sourceSessionPath)
    {
        var (game, gameId) = ResolveGameForSession(sourceSessionPath);
        if (game is null && gameId is null)
            return;

        foreach (var clipFile in clipFiles)
            _clipTitles.SaveGame(Path.GetFileName(clipFile), game, gameId);
    }
}
