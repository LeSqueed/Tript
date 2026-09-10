// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security;

namespace Tript.GameDiscovery;

public sealed class SteamInventorySource(IDiscoveryFileSystem fileSystem, IDiscoveryRegistry registry) : IGameInventorySource
{
    public GameStore Store => GameStore.Steam;

    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new InventoryBuilder(Store);
        var steamRoots = ReadSteamRoots(result).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in steamRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddLibrary(libraries, Path.Combine(root, "steamapps"));
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!fileSystem.FileExists(libraryFile)) continue;
            try
            {
                var parsed = ValveKeyValues.Parse(fileSystem.ReadAllText(libraryFile));
                var folders = parsed.Object("libraryfolders") ?? parsed;
                foreach (var entry in folders.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = entry.Value switch
                    {
                        string legacyPath => legacyPath,
                        ValveKeyValues modern => modern.String("path"),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(path))
                        AddLibrary(libraries, Path.Combine(path, "steamapps"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                result.Warn("steam.libraryfolders", ex.Message, libraryFile);
            }
        }

        foreach (var library in libraries)
        {
            IEnumerable<string> manifests;
            try { manifests = fileSystem.EnumerateFiles(library, "appmanifest_*.acf").ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warn("steam.enumerate", ex.Message, library);
                continue;
            }

            foreach (var manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var state = ValveKeyValues.Parse(fileSystem.ReadAllText(manifest)).Object("AppState");
                    var id = state?.String("appid");
                    var name = state?.String("name");
                    var installDir = state?.String("installdir");
                    if (id is null || name is null || installDir is null ||
                        !PathSafety.TryResolveLexicallyContained(fileSystem, Path.Combine(library, "common"), installDir, out var root))
                    {
                        result.Warn("steam.manifest", "Manifest is missing required fields or its install directory is not lexically contained by the library.", manifest);
                        continue;
                    }
                    result.AddGame(id, name, root);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                {
                    result.Warn("steam.manifest", ex.Message, manifest);
                }
            }
        }
        return ValueTask.FromResult(result.Build());
    }

    private IEnumerable<string> ReadSteamRoots(InventoryBuilder result)
    {
        var locations = new[]
        {
            (RegistryHiveId.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHiveId.LocalMachine, @"Software\Valve\Steam", "InstallPath"),
        };
        foreach (var (hive, key, value) in locations)
        foreach (var view in Enum.GetValues<RegistryViewId>())
        {
            string? path;
            try { path = registry.GetString(hive, view, key, value); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException)
            {
                result.Warn("steam.registry", ex.Message, key);
                continue;
            }
            if (PathSafety.TryCanonicalize(fileSystem, path ?? string.Empty, out var root)) yield return root;
        }
    }

    private void AddLibrary(HashSet<string> libraries, string candidate)
    {
        if (PathSafety.TryCanonicalize(fileSystem, candidate, out var library) && fileSystem.DirectoryExists(library))
            libraries.Add(library);
    }
}
