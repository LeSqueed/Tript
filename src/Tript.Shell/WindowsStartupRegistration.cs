// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Microsoft.Win32;

namespace Tript.Shell;

internal interface IStartupEntryStore
{
    void Set(string name, string commandLine);

    void Delete(string name);
}

internal sealed class RegistryStartupEntryStore : IStartupEntryStore
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    public void Set(string name, string commandLine)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Windows could not open the per-user startup key.");
        key.SetValue(name, commandLine, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

internal sealed class WindowsStartupRegistration
{
    internal const string EntryName = "Tript";
    internal const int MaximumCommandLineLength = 260;

    private readonly IStartupEntryStore _store;
    private readonly bool _isWindows;

    internal WindowsStartupRegistration(IStartupEntryStore store, bool isWindows)
    {
        _store = store;
        _isWindows = isWindows;
    }

    internal static WindowsStartupRegistration Create()
        => new(new RegistryStartupEntryStore(), OperatingSystem.IsWindows());

    internal void Apply(bool enabled, string executablePath)
    {
        if (!_isWindows)
            return;

        if (!enabled)
        {
            _store.Delete(EntryName);
            return;
        }

        var commandLine = BuildCommandLine(executablePath);
        if (commandLine.Length > MaximumCommandLineLength)
        {
            throw new InvalidOperationException(
                "The Tript executable path is too long for Windows per-user startup registration.");
        }

        _store.Set(EntryName, commandLine);
    }

    internal static string BuildCommandLine(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("An executable path is required.", nameof(executablePath));

        return $"\"{executablePath}\" --startup";
    }
}
