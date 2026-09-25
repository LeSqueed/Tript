// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security;
using Tript.Core;

namespace Tript.GameDiscovery;

public sealed class SteamInventorySource : IGameInventorySource
{
    private const string CommonRedistributablesAppId = "228980";
    private const string CompatibilityToolManifest = "toolmanifest.vdf";

    private readonly IDiscoveryFileSystem _fileSystem;
    private readonly Func<InventoryBuilder, IEnumerable<string>> _readSteamRoots;
    private readonly bool _linuxLayout;

    public SteamInventorySource(IDiscoveryFileSystem fileSystem, IDiscoveryRegistry registry)
    {
        _fileSystem = fileSystem;
        _readSteamRoots = result => ReadRegistrySteamRoots(registry, result);
    }

    private SteamInventorySource(IDiscoveryFileSystem fileSystem, string homeDirectory)
    {
        _fileSystem = fileSystem;
        _readSteamRoots = _ => ReadLinuxSteamRoots(homeDirectory);
        _linuxLayout = true;
    }

    public static SteamInventorySource ForLinuxHome(IDiscoveryFileSystem fileSystem, string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return new(fileSystem, homeDirectory);
    }

    public GameStore Store => GameStore.Steam;

    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new InventoryBuilder(Store);
        var steamRoots = _readSteamRoots(result).Distinct(FilePaths.Comparer).ToArray();
        var libraries = new HashSet<string>(FilePaths.Comparer);

        foreach (var root in steamRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddLibrary(libraries, Path.Combine(root, "steamapps"));
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!_fileSystem.FileExists(libraryFile)) continue;
            try
            {
                var parsed = ValveKeyValues.Parse(_fileSystem.ReadAllText(libraryFile));
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
            try { manifests = _fileSystem.EnumerateFiles(library, "appmanifest_*.acf").ToArray(); }
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
                    var state = ValveKeyValues.Parse(_fileSystem.ReadAllText(manifest)).Object("AppState");
                    var id = state?.String("appid");
                    var name = state?.String("name");
                    var installDir = state?.String("installdir");
                    if (id is null || name is null || installDir is null ||
                        !PathSafety.TryResolveLexicallyContained(_fileSystem, Path.Combine(library, "common"), installDir, out var root))
                    {
                        result.Warn("steam.manifest", "Manifest is missing required fields or its install directory is not lexically contained by the library.", manifest);
                        continue;
                    }
                    if (_linuxLayout && IsSteamTooling(id, root))
                        continue;
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

    private bool IsSteamTooling(string appId, string installRoot) =>
        appId.Trim() == CommonRedistributablesAppId
        || _fileSystem.FileExists(Path.Combine(installRoot, CompatibilityToolManifest));

    private IEnumerable<string> ReadRegistrySteamRoots(IDiscoveryRegistry registry, InventoryBuilder result)
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
            if (PathSafety.TryCanonicalize(_fileSystem, path ?? string.Empty, out var root)) yield return root;
        }
    }

    private IEnumerable<string> ReadLinuxSteamRoots(string homeDirectory)
    {
        string[][] candidates =
        [
            [".steam", "steam"],
            [".steam", "root"],
            [".local", "share", "Steam"],
            [".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"],
            ["snap", "steam", "common", ".local", "share", "Steam"],
        ];
        foreach (var parts in candidates)
        {
            if (TryResolveExistingDirectory(Path.Combine([homeDirectory, .. parts]), out var root))
                yield return root;
        }
    }

    private void AddLibrary(HashSet<string> libraries, string candidate)
    {
        if (_linuxLayout)
        {
            if (TryResolveExistingDirectory(candidate, out var resolved))
                libraries.Add(resolved);
            return;
        }

        if (PathSafety.TryCanonicalize(_fileSystem, candidate, out var library) && _fileSystem.DirectoryExists(library))
            libraries.Add(library);
    }

    private bool TryResolveExistingDirectory(string candidate, out string resolved) =>
        PathSafety.TryResolveExistingDirectory(_fileSystem, candidate, out resolved);
}
