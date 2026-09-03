// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.GameDiscovery;
using Tript.Recorder;
using Tript.Core;

namespace Tript.App;

internal sealed record GameCatalogEntry(
    string GameId,
    string Executable,
    string? Name = null,
    IReadOnlyList<GameStoreProduct>? StoreProducts = null)
{
    internal bool HasStoreProduct(GameStore store, string productId)
        => StoreProducts is { } products &&
            products.Any(p => string.Equals(p.Store, store.ToString(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.ProductId, productId, StringComparison.OrdinalIgnoreCase));
}

internal sealed record GameStoreProduct(string Store, string ProductId);

internal sealed class GameCatalog
{
    private GameCatalog(IReadOnlyList<GameCatalogEntry> entries)
    {
        Entries = entries;
    }

    internal IReadOnlyList<GameCatalogEntry> Entries { get; }

    internal GameCatalogEntry? EntryById(string gameId)
        => Entries.FirstOrDefault(entry =>
            string.Equals(entry.GameId, gameId, StringComparison.OrdinalIgnoreCase));

    internal static GameCatalog Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The packaged game catalogue was not found.", path);

        var entries = JsonSerializer.Deserialize<List<GameCatalogEntry>>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        Validate(entries);
        return new GameCatalog(entries);
    }

    private static void Validate(IReadOnlyList<GameCatalogEntry> entries)
    {
        var gameIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executables = new HashSet<string>(ExecutableNames.Comparer);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.GameId) || string.IsNullOrWhiteSpace(entry.Executable))
                throw new InvalidDataException("Every game catalogue entry requires a gameId and executable.");
            if (FilePaths.ContainsSeparator(entry.Executable))
            {
                throw new InvalidDataException(
                    $"Game catalogue executable '{entry.Executable}' must be a filename, not a path.");
            }
            if (!gameIds.Add(entry.GameId))
                throw new InvalidDataException($"Game catalogue contains duplicate gameId '{entry.GameId}'.");
            if (!executables.Add(ExecutableNames.Normalize(entry.Executable)))
                throw new InvalidDataException(
                    $"Game catalogue contains duplicate executable '{entry.Executable}'.");

            if (entry.StoreProducts is null)
                continue;

            foreach (var product in entry.StoreProducts)
            {
                if (string.IsNullOrWhiteSpace(product.Store) || string.IsNullOrWhiteSpace(product.ProductId))
                {
                    throw new InvalidDataException(
                        $"Game catalogue entry '{entry.GameId}' has a store product with a blank store or product id.");
                }
            }
        }
    }
}
