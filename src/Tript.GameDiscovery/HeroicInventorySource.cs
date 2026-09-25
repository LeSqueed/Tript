// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Core;

namespace Tript.GameDiscovery;

public sealed class HeroicInventorySource : IGameInventorySource
{
    private readonly IDiscoveryFileSystem _fileSystem;
    private readonly string _homeDirectory;

    public HeroicInventorySource(IDiscoveryFileSystem fileSystem, string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        _fileSystem = fileSystem;
        _homeDirectory = homeDirectory;
    }

    public GameStore Store => GameStore.Epic;

    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new InventoryBuilder(Store);
        foreach (var configRoot in ConfigRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadLegendaryGames(Path.Combine(configRoot, "legendaryConfig", "legendary"), result, cancellationToken);
            ReadGogGames(Path.Combine(configRoot, "gog_store"), result, cancellationToken);
        }
        return ValueTask.FromResult(result.Build());
    }

    private IEnumerable<string> ConfigRoots()
    {
        string[][] candidates =
        [
            [".config", "heroic"],
            [".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic"],
        ];
        return candidates
            .Select(parts => PathSafety.TryResolveExistingDirectory(_fileSystem, Path.Combine([_homeDirectory, .. parts]), out var root)
                ? root
                : null)
            .OfType<string>()
            .Distinct(FilePaths.Comparer)
            .ToArray();
    }

    private void ReadLegendaryGames(string legendaryRoot, InventoryBuilder result, CancellationToken cancellationToken)
    {
        var installedFile = Path.Combine(legendaryRoot, "installed.json");
        if (!_fileSystem.FileExists(installedFile))
            return;

        try
        {
            using var json = JsonDocument.Parse(_fileSystem.ReadAllText(installedFile));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("installed.json is not an object keyed by app name.");
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Value.ValueKind == JsonValueKind.Object)
                    AddLegendaryGame(entry.Name, entry.Value, legendaryRoot, result, installedFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result.Warn(GameStore.Epic, "heroic.legendary", ex.Message, installedFile);
        }
    }

    private void AddLegendaryGame(string key, JsonElement item, string legendaryRoot, InventoryBuilder result, string location)
    {
        if (Boolean(item, "is_dlc"))
            return;

        var appName = String(item, "app_name") ?? key;
        var title = String(item, "title") ?? appName;
        if (!TryInstallRoot(String(item, "install_path"), out var root))
        {
            result.Warn(GameStore.Epic, "heroic.legendary", "Entry is missing its install path.", location);
            return;
        }

        var executables = new List<string>();
        var declared = String(item, "executable");
        if (!string.IsNullOrWhiteSpace(declared))
        {
            if (!PathSafety.TryResolveLexicallyContained(_fileSystem, root, declared, out var executable))
                result.Warn(GameStore.Epic, "heroic.executable", "Executable is not lexically contained by the install root.", location);
            else if (_fileSystem.FileExists(executable))
                executables.Add(executable);
        }

        var productId = CatalogItemId(legendaryRoot, appName, result) ?? appName;
        result.AddGame(GameStore.Epic, productId, title, root, executables);
    }

    private string? CatalogItemId(string legendaryRoot, string appName, InventoryBuilder result)
    {
        if (!PathSafety.TryResolveLexicallyContained(_fileSystem, Path.Combine(legendaryRoot, "metadata"), appName + ".json", out var metadataFile)
            || !_fileSystem.FileExists(metadataFile))
            return null;

        try
        {
            using var json = JsonDocument.Parse(_fileSystem.ReadAllText(metadataFile));
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object
                    ? String(metadata, "id")
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result.Warn(GameStore.Epic, "heroic.metadata", ex.Message, metadataFile);
            return null;
        }
    }

    private void ReadGogGames(string gogRoot, InventoryBuilder result, CancellationToken cancellationToken)
    {
        var installedFile = Path.Combine(gogRoot, "installed.json");
        if (!_fileSystem.FileExists(installedFile))
            return;

        var titles = ReadGogTitles(Path.Combine(gogRoot, "library.json"), result);
        try
        {
            using var json = JsonDocument.Parse(_fileSystem.ReadAllText(installedFile));
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("installed", out var installed)
                || installed.ValueKind != JsonValueKind.Array)
                throw new JsonException("installed.json has no installed array.");
            foreach (var item in installed.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.ValueKind == JsonValueKind.Object)
                    AddGogGame(item, titles, result, installedFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result.Warn(GameStore.Gog, "heroic.gog", ex.Message, installedFile);
        }
    }

    private void AddGogGame(JsonElement item, Dictionary<string, string> titles, InventoryBuilder result, string location)
    {
        var appName = String(item, "appName");
        if (string.IsNullOrWhiteSpace(appName)
            || !TryInstallRoot(String(item, "install_path"), out var root))
        {
            result.Warn(GameStore.Gog, "heroic.gog", "Entry is missing its app name or install path.", location);
            return;
        }

        var title = titles.GetValueOrDefault(appName.Trim()) ?? Path.GetFileName(root);
        result.AddGame(GameStore.Gog, appName, title, root);
    }

    private Dictionary<string, string> ReadGogTitles(string libraryFile, InventoryBuilder result)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_fileSystem.FileExists(libraryFile))
            return titles;

        try
        {
            using var json = JsonDocument.Parse(_fileSystem.ReadAllText(libraryFile));
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("games", out var games)
                || games.ValueKind != JsonValueKind.Array)
                return titles;
            foreach (var game in games.EnumerateArray())
            {
                if (game.ValueKind != JsonValueKind.Object)
                    continue;
                var appName = String(game, "app_name");
                var title = String(game, "title");
                if (!string.IsNullOrWhiteSpace(appName) && !string.IsNullOrWhiteSpace(title))
                    titles.TryAdd(appName.Trim(), title);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result.Warn(GameStore.Gog, "heroic.gog.library", ex.Message, libraryFile);
        }
        return titles;
    }

    private bool TryInstallRoot(string? installPath, out string root)
    {
        root = string.Empty;
        return installPath is not null
            && Path.IsPathFullyQualified(installPath.Trim())
            && PathSafety.TryCanonicalize(_fileSystem, installPath, out root);
    }

    private static string? String(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Boolean(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
