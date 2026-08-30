// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace Tript.GameDiscovery;

public sealed class EpicInventorySource(IDiscoveryFileSystem fileSystem, IEnumerable<string> metadataLocations) : IGameInventorySource
{
    public GameStore Store => GameStore.Epic;

    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new InventoryBuilder(Store);
        foreach (var location in metadataLocations.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fileSystem.DirectoryExists(location))
            {
                try
                {
                    foreach (var item in fileSystem.EnumerateFiles(location, "*.item").ToArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ParseItem(item, result);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Warn("epic.enumerate", ex.Message, location);
                }
            }
            else if (fileSystem.FileExists(location))
            {
                ParseInstalledData(location, result, cancellationToken);
            }
        }
        return ValueTask.FromResult(result.Build());
    }

    private void ParseItem(string path, InventoryBuilder result)
    {
        try
        {
            using var json = JsonDocument.Parse(fileSystem.ReadAllText(path));
            AddJsonGame(json.RootElement, result, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result.Warn("epic.item", ex.Message, path);
        }
    }

    private void ParseInstalledData(string path, InventoryBuilder result, CancellationToken cancellationToken)
    {
        try
        {
            using var json = JsonDocument.Parse(fileSystem.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("InstallationList", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new JsonException("LauncherInstalled.dat has no InstallationList array.");
            foreach (var item in list.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddJsonGame(item, result, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result.Warn("epic.installed", ex.Message, path);
        }
    }

    private void AddJsonGame(JsonElement item, InventoryBuilder result, string location)
    {
        var id = String(item, "CatalogItemId") ?? String(item, "AppName");
        var name = String(item, "DisplayName") ?? String(item, "AppName");
        var install = String(item, "InstallLocation");
        if (id is null || name is null || install is null || !PathSafety.TryCanonicalize(fileSystem, install, out var root))
        {
            result.Warn("epic.metadata", "Entry is missing its product ID, name, or install location.", location);
            return;
        }

        var executables = new List<string>();
        var declared = String(item, "LaunchExecutable");
        if (declared is not null)
        {
            if (!PathSafety.TryResolveLexicallyContained(fileSystem, root, declared, out var executable))
                result.Warn("epic.executable", "LaunchExecutable is not lexically contained by the install root.", location);
            else if (!fileSystem.FileExists(executable))
                result.Warn("epic.executable", "LaunchExecutable does not identify an existing regular file.", location);
            else
                executables.Add(executable);
        }
        result.AddGame(id, name, root, executables);
    }

    private static string? String(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
