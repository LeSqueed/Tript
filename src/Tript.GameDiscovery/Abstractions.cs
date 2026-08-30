// SPDX-License-Identifier: GPL-2.0-or-later

namespace Tript.GameDiscovery;

public interface IGameInventorySource
{
    GameStore Store { get; }

    ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface IDiscoveryFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string ReadAllText(string path);
    IEnumerable<string> EnumerateFiles(string path, string searchPattern);
    IEnumerable<string> EnumerateDirectories(string path);
    string GetFullPath(string path);
}

public enum RegistryHiveId
{
    CurrentUser,
    LocalMachine,
}

public enum RegistryViewId
{
    Registry32,
    Registry64,
}

public interface IDiscoveryRegistry
{
    string? GetString(RegistryHiveId hive, RegistryViewId view, string keyPath, string valueName);
    IEnumerable<string> GetSubKeyNames(RegistryHiveId hive, RegistryViewId view, string keyPath);
}

public interface IFixedDriveProvider
{
    IEnumerable<string> GetFixedDriveRoots();
}

public sealed record XboxPackage(
    string ProductId,
    string DisplayName,
    string InstallRoot,
    IReadOnlyList<string> DeclaredExecutablePaths);

public interface IXboxPackageProvider
{
    IEnumerable<XboxPackage> GetInstalledPackages(CancellationToken cancellationToken = default);
}
