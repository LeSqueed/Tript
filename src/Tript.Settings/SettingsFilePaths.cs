// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// The platform config directory under the product name. These directories rename with the
// project (the original used its own name; the charter records that the config directory renames
// with the project and needs a migration path — charter/branding.md), so they are named Tript,
// never the original's name.
using System.Runtime.InteropServices;

namespace Tript.Settings;

public static class SettingsFilePaths
{
    public const string DirectoryName = "Tript";

    public const string SettingsFileName = "settings.json";

    // The settings file on this platform, creating nothing.
    public static string SettingsPath => Path.Combine(ConfigDirectory, SettingsFileName);

    // $XDG_CONFIG_HOME/Tript, falling back to $HOME/.config/Tript on Linux; %AppData%\Tript on
    // Windows.
    public static string ConfigDirectory => _configDirectory.Value;

    private static readonly Lazy<string> _configDirectory = new(ComputeConfigDirectory);

    // On the first read the directory is created, because the caller is about to write into it.
    // Creating it here keeps every consumer — settings, recordings, data feeds, logs — agreeing
    // on one place without each recreating the policy.
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
