// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Recorder;

namespace Tript.App;

internal sealed record GameCatalogEntry(string GameId, string Executable);

internal sealed class GameCatalog
{
    private GameCatalog(IReadOnlyList<GameCatalogEntry> entries)
    {
        Entries = entries;
    }

    internal IReadOnlyList<GameCatalogEntry> Entries { get; }

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
        var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.GameId) || string.IsNullOrWhiteSpace(entry.Executable))
                throw new InvalidDataException("Every game catalogue entry requires a gameId and executable.");
            if (entry.Executable.Contains(Path.DirectorySeparatorChar)
                || entry.Executable.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new InvalidDataException(
                    $"Game catalogue executable '{entry.Executable}' must be a filename, not a path.");
            }
            if (!gameIds.Add(entry.GameId))
                throw new InvalidDataException($"Game catalogue contains duplicate gameId '{entry.GameId}'.");
            if (!executables.Add(ProcessNameGameDetector.NormalizeProcessName(entry.Executable)))
                throw new InvalidDataException(
                    $"Game catalogue contains duplicate executable '{entry.Executable}'.");
        }
    }
}
