// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tript.Settings.Tests;

public class UnreadableSettingsRecoveryTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public UnreadableSettingsRecoveryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "tript-unreadable-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Load runs during startup, so a parse failure that escaped it stopped Tript from starting at all.
    [Fact]
    public void ATruncatedFileFallsBackToDefaultsInsteadOfThrowing()
    {
        File.WriteAllText(_path, "{ \"recording\": { \"resolutionWidth\": 19");

        var settings = new SettingsStore(new SettingsFileProvider(_path)).Load();

        Assert.NotNull(settings);
    }

    // Falling back to defaults and then saving would overwrite the user's only copy of their
    // settings, so the unreadable file has to be moved aside before anything can write over it.
    [Fact]
    public void TheUnreadableFileIsKeptAsideSoASaveCannotOverwriteIt()
    {
        const string original = "{ this is not json";
        File.WriteAllText(_path, original);

        var store = new SettingsStore(new SettingsFileProvider(_path));
        store.Load();
        store.Save();

        Assert.NotNull(store.RecoveredFrom);
        Assert.Equal(original, File.ReadAllText(store.RecoveredFrom!));
        Assert.Single(Directory.EnumerateFiles(_directory, "settings.json.unreadable-*"));
    }

    [Fact]
    public void AValidFileIsLoadedAndNothingIsMovedAside()
    {
        var store = new SettingsStore(new SettingsFileProvider(_path));
        store.Load();
        store.Save();

        var reloaded = new SettingsStore(new SettingsFileProvider(_path));
        reloaded.Load();

        Assert.Null(reloaded.RecoveredFrom);
        Assert.Empty(Directory.EnumerateFiles(_directory, "settings.json.unreadable-*"));
    }
}
