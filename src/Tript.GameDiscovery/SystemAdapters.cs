// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Tript.GameDiscovery;

public sealed class PhysicalDiscoveryFileSystem : IDiscoveryFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern) =>
        Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
    public IEnumerable<string> EnumerateDirectories(string path) =>
        Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly);
    public string GetFullPath(string path) => Path.GetFullPath(path);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsDiscoveryRegistry : IDiscoveryRegistry
{
    public string? GetString(RegistryHiveId hive, RegistryViewId view, string keyPath, string valueName)
    {
        using var baseKey = RegistryKey.OpenBaseKey(ToHive(hive), ToView(view));
        using var key = baseKey.OpenSubKey(keyPath);
        return key?.GetValue(valueName) as string;
    }

    public IEnumerable<string> GetSubKeyNames(RegistryHiveId hive, RegistryViewId view, string keyPath)
    {
        using var baseKey = RegistryKey.OpenBaseKey(ToHive(hive), ToView(view));
        using var key = baseKey.OpenSubKey(keyPath);
        return key?.GetSubKeyNames() ?? [];
    }

    private static RegistryHive ToHive(RegistryHiveId hive) => hive switch
    {
        RegistryHiveId.CurrentUser => RegistryHive.CurrentUser,
        RegistryHiveId.LocalMachine => RegistryHive.LocalMachine,
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    private static RegistryView ToView(RegistryViewId view) => view switch
    {
        RegistryViewId.Registry32 => RegistryView.Registry32,
        RegistryViewId.Registry64 => RegistryView.Registry64,
        _ => throw new ArgumentOutOfRangeException(nameof(view)),
    };
}

public sealed class FixedDriveProvider : IFixedDriveProvider
{
    public IEnumerable<string> GetFixedDriveRoots() => DriveInfo.GetDrives()
        .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
        .Select(drive => drive.RootDirectory.FullName);
}
