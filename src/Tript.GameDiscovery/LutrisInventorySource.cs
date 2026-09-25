// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;

namespace Tript.GameDiscovery;

public sealed record LutrisGameRow(string? Name, string? Directory, string? Service, string? ServiceId);

public interface ILutrisGameRows
{
    IReadOnlyList<LutrisGameRow> InstalledGames(string databasePath);
}

public sealed class SqliteLutrisGameRows : ILutrisGameRows
{
    private const string InstalledGamesQuery =
        "SELECT name, directory, service, service_id FROM games WHERE installed = 1";

    public IReadOnlyList<LutrisGameRow> InstalledGames(string databasePath) =>
        SqliteReadOnlyQuery.Rows(databasePath, InstalledGamesQuery)
            .Select(row => new LutrisGameRow(row[0], row[1], row[2], row[3]))
            .ToArray();
}

public sealed class LutrisInventorySource : IGameInventorySource
{
    private readonly IDiscoveryFileSystem _fileSystem;
    private readonly string _homeDirectory;
    private readonly ILutrisGameRows _rows;

    public LutrisInventorySource(IDiscoveryFileSystem fileSystem, string homeDirectory, ILutrisGameRows? rows = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        _fileSystem = fileSystem;
        _homeDirectory = homeDirectory;
        _rows = rows ?? new SqliteLutrisGameRows();
    }

    public GameStore Store => GameStore.Local;

    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new InventoryBuilder(Store);
        foreach (var database in DatabasePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<LutrisGameRow> rows;
            try { rows = _rows.InstalledGames(database); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                or IOException or UnauthorizedAccessException)
            {
                result.Warn("lutris.database", ex.Message, database);
                continue;
            }

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddGame(row, result);
            }
        }
        return ValueTask.FromResult(result.Build());
    }

    private void AddGame(LutrisGameRow row, InventoryBuilder result)
    {
        var directory = row.Directory?.Trim() ?? string.Empty;
        if (!Path.IsPathFullyQualified(directory) || !PathSafety.TryCanonicalize(_fileSystem, directory, out var root))
            return;

        var name = string.IsNullOrWhiteSpace(row.Name) ? Path.GetFileName(root) : row.Name;
        if (StoreOf(row.Service) is { } store && !string.IsNullOrWhiteSpace(row.ServiceId))
            result.AddGame(store, row.ServiceId, name, root);
        else
            result.AddGame(GameStore.Local, root, name, root);
    }

    private static GameStore? StoreOf(string? service) => service?.Trim().ToLowerInvariant() switch
    {
        "steam" => GameStore.Steam,
        "egs" => GameStore.Epic,
        "gog" => GameStore.Gog,
        _ => null,
    };

    private IEnumerable<string> DatabasePaths()
    {
        string[][] candidates =
        [
            [".local", "share", "lutris", "pga.db"],
            [".var", "app", "net.lutris.Lutris", "data", "lutris", "pga.db"],
        ];
        return candidates
            .Select(parts => Path.Combine([_homeDirectory, .. parts]))
            .Where(_fileSystem.FileExists)
            .Select(ResolvedFile)
            .Distinct(FilePaths.Comparer)
            .ToArray();
    }

    private string ResolvedFile(string path)
    {
        try { return _fileSystem.ResolveLinks(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }
}
