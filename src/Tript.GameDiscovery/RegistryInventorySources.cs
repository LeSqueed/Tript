// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security;

namespace Tript.GameDiscovery;

public sealed class EaInventorySource(IDiscoveryFileSystem fileSystem, IDiscoveryRegistry registry) : IGameInventorySource
{
    private static readonly string[] ParentKeys = [@"Software\EA Games", @"Software\Electronic Arts\EA Games"];
    public GameStore Store => GameStore.EA;

    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new InventoryBuilder(Store);
        foreach (var view in Enum.GetValues<RegistryViewId>())
        foreach (var parent in ParentKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> products;
            try { products = registry.GetSubKeyNames(RegistryHiveId.LocalMachine, view, parent).ToArray(); }
            catch (Exception ex) when (RegistryExceptions.IsExpected(ex))
            {
                result.Warn("ea.registry", ex.Message, parent);
                continue;
            }
            foreach (var product in products)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = $@"{parent}\{product}";
                try
                {
                    var install = First(view, key, "Install Dir", "InstallDir", "InstallLocation");
                    var id = First(view, key, "ProductId", "Product ID") ?? product;
                    var name = First(view, key, "DisplayName", "Display Name") ?? product;
                    if (install is not null && PathSafety.TryCanonicalize(fileSystem, install, out var root))
                        result.AddGame(id, name, root);
                }
                catch (Exception ex) when (RegistryExceptions.IsExpected(ex))
                {
                    result.Warn("ea.registry", ex.Message, key);
                }
            }
        }
        return ValueTask.FromResult(result.Build());
    }

    private string? First(RegistryViewId view, string key, params string[] names) =>
        names.Select(name => registry.GetString(RegistryHiveId.LocalMachine, view, key, name))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed class UbisoftInventorySource(IDiscoveryFileSystem fileSystem, IDiscoveryRegistry registry) : IGameInventorySource
{
    private const string ParentKey = @"Software\Ubisoft\Launcher\Installs";
    public GameStore Store => GameStore.Ubisoft;

    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new InventoryBuilder(Store);
        foreach (var view in Enum.GetValues<RegistryViewId>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> products;
            try { products = registry.GetSubKeyNames(RegistryHiveId.LocalMachine, view, ParentKey).ToArray(); }
            catch (Exception ex) when (RegistryExceptions.IsExpected(ex))
            {
                result.Warn("ubisoft.registry", ex.Message, ParentKey);
                continue;
            }
            foreach (var product in products)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = $@"{ParentKey}\{product}";
                try
                {
                    var install = registry.GetString(RegistryHiveId.LocalMachine, view, key, "InstallDir");
                    var name = registry.GetString(RegistryHiveId.LocalMachine, view, key, "DisplayName") ?? product;
                    if (install is not null && PathSafety.TryCanonicalize(fileSystem, install, out var root))
                        result.AddGame(product, name, root);
                }
                catch (Exception ex) when (RegistryExceptions.IsExpected(ex))
                {
                    result.Warn("ubisoft.registry", ex.Message, key);
                }
            }
        }
        return ValueTask.FromResult(result.Build());
    }
}

public sealed class BattleNetInventorySource(IDiscoveryFileSystem fileSystem, IDiscoveryRegistry registry) : IGameInventorySource
{
    private const string ParentKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string LauncherUid = "battle.net";
    public GameStore Store => GameStore.BattleNet;

    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new InventoryBuilder(Store);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in Enum.GetValues<RegistryViewId>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> entries;
            try { entries = registry.GetSubKeyNames(RegistryHiveId.LocalMachine, view, ParentKey).ToArray(); }
            catch (Exception ex) when (RegistryExceptions.IsExpected(ex))
            {
                result.Warn("battlenet.registry", ex.Message, ParentKey);
                continue;
            }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = $@"{ParentKey}\{entry}";
                try
                {
                    var uninstall = registry.GetString(RegistryHiveId.LocalMachine, view, key, "UninstallString");
                    if (UidOf(uninstall) is not { } uid || uid.Equals(LauncherUid, StringComparison.OrdinalIgnoreCase)
                        || !seen.Add(uid))
                    {
                        continue;
                    }

                    var install = registry.GetString(RegistryHiveId.LocalMachine, view, key, "InstallLocation");
                    var name = registry.GetString(RegistryHiveId.LocalMachine, view, key, "DisplayName") ?? entry;
                    if (install is not null && PathSafety.TryCanonicalize(fileSystem, install, out var root))
                        result.AddGame(uid, name, root);
                }
                catch (Exception ex) when (RegistryExceptions.IsExpected(ex))
                {
                    result.Warn("battlenet.registry", ex.Message, key);
                }
            }
        }
        return ValueTask.FromResult(result.Build());
    }

    private static string? UidOf(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString)
            || !uninstallString.Contains("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        const string flag = "--uid=";
        var start = uninstallString.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += flag.Length;
        var end = uninstallString.IndexOfAny([' ', '"'], start);
        var uid = (end < 0 ? uninstallString[start..] : uninstallString[start..end]).Trim();
        return uid.Length == 0 ? null : uid;
    }
}

internal static class RegistryExceptions
{
    public static bool IsExpected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException;
}
