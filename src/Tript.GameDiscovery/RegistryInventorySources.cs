// SPDX-License-Identifier: GPL-2.0-or-later

namespace Tript.GameDiscovery;

using System.Security;

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
            catch (Exception ex) when (IsExpectedRegistryException(ex))
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
                catch (Exception ex) when (IsExpectedRegistryException(ex))
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

    private static bool IsExpectedRegistryException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException;
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
            catch (Exception ex) when (IsExpectedRegistryException(ex))
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
                catch (Exception ex) when (IsExpectedRegistryException(ex))
                {
                    result.Warn("ubisoft.registry", ex.Message, key);
                }
            }
        }
        return ValueTask.FromResult(result.Build());
    }

    private static bool IsExpectedRegistryException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException;
}
