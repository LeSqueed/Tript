// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;

namespace Tript.App.Models;

internal sealed class GameIdAliasStore
{
    private const int MaxChainHops = 8;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, string> _aliases;

    internal GameIdAliasStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(GameModelPaths.DataRoot, "game-id-aliases.json"));
        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (oldId, newId) in Load(_path))
        {
            if (!string.IsNullOrWhiteSpace(oldId) && !string.IsNullOrWhiteSpace(newId))
                _aliases[oldId] = newId;
        }
    }

    internal string? Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        string? current = null;
        var next = id;
        lock (_gate)
        {
            for (var hop = 0; hop < MaxChainHops && _aliases.TryGetValue(next, out var mapped); hop++)
            {
                current = mapped;
                next = mapped;
            }
        }

        return current;
    }

    internal bool TryAdd(string oldId, string newId)
    {
        if (string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(newId) ||
            string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (_gate)
        {
            _aliases[oldId] = newId;
            try
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                var temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new AliasFile
                {
                    SchemaVersion = 1,
                    Aliases = _aliases,
                }, JsonOptions));
                File.Move(temporaryPath, _path, true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static IReadOnlyDictionary<string, string> Load(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<AliasFile>(File.ReadAllText(path), JsonOptions)?.Aliases
                ?? new Dictionary<string, string>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private sealed class AliasFile
    {
        public int SchemaVersion { get; init; }
        public Dictionary<string, string> Aliases { get; init; } = new();
    }
}
