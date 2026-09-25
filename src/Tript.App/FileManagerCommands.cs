// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.App;

internal enum DesktopPlatform
{
    Windows,
    MacOS,
    Linux,
}

internal static class FileManagerCommands
{
    internal static DesktopPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? DesktopPlatform.Windows
        : OperatingSystem.IsMacOS() ? DesktopPlatform.MacOS
        : DesktopPlatform.Linux;

    internal static ProcessStartInfo RevealFile(string path, DesktopPlatform platform) => platform switch
    {
        DesktopPlatform.Windows => Explorer($"/select,\"{WithoutQuotes(path)}\""),
        DesktopPlatform.MacOS => Command("open", "-R", path),
        _ => Command("xdg-open", Path.GetDirectoryName(path)!),
    };

    internal static ProcessStartInfo OpenFolder(string directory, DesktopPlatform platform) => platform switch
    {
        DesktopPlatform.Windows => Explorer($"\"{WithoutQuotes(directory)}\""),
        DesktopPlatform.MacOS => Command("open", directory),
        _ => Command("xdg-open", directory),
    };

    internal static void Launch(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
    }

    private static ProcessStartInfo Explorer(string arguments) => new()
    {
        FileName = "explorer.exe",
        Arguments = arguments,
        UseShellExecute = true,
    };

    private static ProcessStartInfo Command(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static string WithoutQuotes(string path) => path.Replace("\"", string.Empty);
}
