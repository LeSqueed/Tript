// SPDX-License-Identifier: GPL-2.0-or-later

namespace Tript.GameDiscovery.Tests;

internal sealed class TempFixture : IDisposable
{
    public TempFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "Tript.GameDiscovery.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string DirectoryPath(params string[] parts)
    {
        var path = parts.Aggregate(Root, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }
    public string FilePath(string content, params string[] parts)
    {
        var path = parts.Aggregate(Root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
    public void Dispose() => Directory.Delete(Root, true);
}

internal sealed class FakeRegistry : IDiscoveryRegistry
{
    private readonly Dictionary<(RegistryHiveId, RegistryViewId, string, string), string> _values = new();
    private readonly Dictionary<(RegistryHiveId, RegistryViewId, string), string[]> _subkeys = new();
    public Exception? Failure { get; set; }

    public void Value(RegistryHiveId hive, RegistryViewId view, string key, string name, string value) =>
        _values[(hive, view, key, name)] = value;
    public void SubKeys(RegistryHiveId hive, RegistryViewId view, string key, params string[] values) =>
        _subkeys[(hive, view, key)] = values;
    public string? GetString(RegistryHiveId hive, RegistryViewId view, string keyPath, string valueName)
    {
        if (Failure is not null) throw Failure;
        return _values.GetValueOrDefault((hive, view, keyPath, valueName));
    }
    public IEnumerable<string> GetSubKeyNames(RegistryHiveId hive, RegistryViewId view, string keyPath)
    {
        if (Failure is not null) throw Failure;
        return _subkeys.GetValueOrDefault((hive, view, keyPath)) ?? [];
    }
}

internal sealed class FakeDrives(params string[] roots) : IFixedDriveProvider
{
    public IEnumerable<string> GetFixedDriveRoots() => roots;
}

internal sealed class ThrowingSource(GameStore store) : IGameInventorySource
{
    public GameStore Store => store;
    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default) =>
        throw new IOException("broken source");
}

internal sealed class StaticSource(InstalledGame game) : IGameInventorySource
{
    public GameStore Store => game.Store;
    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new SourceInventory([game], []));
}

internal sealed class FakeXboxPackages(params XboxPackage[] packages) : IXboxPackageProvider
{
    public IEnumerable<XboxPackage> GetInstalledPackages(CancellationToken cancellationToken = default) => packages;
}
