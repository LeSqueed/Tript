// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Models;
using Tript.Core;

namespace Tript.App;

internal sealed class LibraryGames
{
    private readonly Dictionary<string, GameInfo> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly GameIdAliasStore _aliases;

    internal LibraryGames(IReadOnlyList<GameInfo> games, GameIdAliasStore aliases)
    {
        All = games;
        _aliases = aliases;
        foreach (var game in games)
            _byId.TryAdd(game.Id, game);
    }

    internal IReadOnlyList<GameInfo> All { get; }

    internal static string ExecutableOf(GameInfo game) => game.Executable ?? game.Name;

    internal GameInfo? Find(string? gameId) =>
        string.IsNullOrEmpty(gameId) ? null : _byId.GetValueOrDefault(gameId);

    internal GameInfo? FindByIdOrName(string value) =>
        All.FirstOrDefault(game =>
            string.Equals(game.Id, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(game.Name, value, StringComparison.OrdinalIgnoreCase));

    internal string? ResolveLegacyGameId(string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName))
            return null;

        return FindByIdOrName(gameName)?.Id
            ?? All.FirstOrDefault(game => ExecutableNames.Equal(ExecutableOf(game), gameName))?.Id;
    }

    internal string? ResolveStoredGameId(string? storedId, string? gameName) =>
        string.IsNullOrWhiteSpace(storedId)
            ? ResolveLegacyGameId(gameName)
            : _aliases.Resolve(storedId) ?? ResolveLegacyGameId(storedId) ?? storedId;

    internal string? DisplayName(string? storedName, string? gameId)
    {
        var name = Find(gameId)?.Name;
        return string.IsNullOrWhiteSpace(name) ? storedName : name;
    }
}
