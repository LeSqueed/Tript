// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Settings;

public static class SettingsFilePaths
{
    public const string DirectoryName = "Tript";

    public const string SettingsFileName = "settings.json";

    public const string WindowStateFileName = "window.json";

    public static string SettingsPath => Path.Combine(ConfigDirectory, SettingsFileName);

    public static string WindowStatePath => Path.Combine(ConfigDirectory, WindowStateFileName);

    public static string ConfigDirectory => _configDirectory.Value;

    private static readonly Lazy<string> _configDirectory = new(ComputeConfigDirectory);

    public static string EnsureConfigDirectory()
    {
        var directory = ConfigDirectory;
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ComputeConfigDirectory()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdg))
                return Path.Combine(xdg, DirectoryName);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", DirectoryName);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, DirectoryName);
    }
}
