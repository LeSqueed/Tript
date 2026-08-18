// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

// GameSetting.Name used to be the display name AND the process name, which was self-consistent and
// therefore worked — but made a game whose executable differs from its title ("Counter-Strike 2" /
// cs2.exe) impossible to list: game capture would look for "Counter-Strike 2.exe" and auto-detect
// for a process called "Counter-Strike 2". These pin that detection and hooking now key off
// Executable while the display name stays Name.
public sealed class GameExecutableRoutingTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly SettingsStore _store;
    private readonly AppHost _host;

    public GameExecutableRoutingTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(GameExecutableRoutingTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        var settingsPath = Path.Combine(_contentRoot, "settings.json");
        _store = new SettingsStore(new SettingsFileProvider(settingsPath));

        _host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = settingsPath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, _store, runtime: null, new RecordingSessionTracker());
    }

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Catalogue(params GameSetting[] games)
    {
        var list = _store.Load().Game.GameList;
        list.Clear();
        list.AddRange(games);
        _host.ReloadGameList();
    }

    [Fact]
    public void ACatalogueEntryWithNoExecutable_CarriesTheDisplayNameAsOne()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("Overwatch", Assert.Single(_host.GameList).Executable);
    }

    [Fact]
    public void ACatalogueEntryWithAnExecutable_KeepsTheDisplayNameSeparate()
    {
        Catalogue(new GameSetting { Id = "cs2", Name = "Counter-Strike 2", Executable = "cs2.exe" });

        var game = Assert.Single(_host.GameList);
        Assert.Equal("Counter-Strike 2", game.Name);
        Assert.Equal("cs2.exe", game.Executable);
    }

    // The gameList push spells it `executable`, camelCase like every other field on the wire.
    [Fact]
    public void TheGameListPush_SpellsItExecutable()
    {
        Catalogue(new GameSetting { Id = "cs2", Name = "Counter-Strike 2", Executable = "cs2.exe" });

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(_host.GameList, Wire.Options));

        var entry = document.RootElement[0];
        Assert.Equal("Counter-Strike 2", entry.GetProperty("name").GetString());
        Assert.Equal("cs2.exe", entry.GetProperty("executable").GetString());
    }

    // ---- auto-start ----
    // ProcessNameGameDetector reports the normalized PROCESS name, and everything downstream of
    // StartRecording (per-game settings, the metadata record's display name, the detection model)
    // looks its game up by Id. Before Executable existed those two agreed only because Id, Name and
    // the process name were all the same string.

    [Fact]
    public void ADetectedProcess_ResolvesToTheGamesId_NotItsProcessName()
    {
        Catalogue(new GameSetting { Id = "cs2-id", Name = "Counter-Strike 2", Executable = "cs2.exe" });

        Assert.Equal("cs2-id", _host.ResolveDetectedGameId("cs2"));
    }

    // The detector strips a trailing .exe from both sides, so an entry may spell the executable
    // either way and the caller is never asked to guess which.
    [Theory]
    [InlineData("cs2.exe")]
    [InlineData("cs2")]
    [InlineData("CS2.EXE")]
    public void AnExecutableSpelledEitherWay_ResolvesTheSameGame(string executable)
    {
        Catalogue(new GameSetting { Id = "cs2-id", Name = "Counter-Strike 2", Executable = executable });

        Assert.Equal("cs2-id", _host.ResolveDetectedGameId("cs2"));
    }

    // The path that worked before Executable existed, still working: no executable set, so the
    // display name is what the detector watches for.
    [Fact]
    public void AGameWithNoExecutable_IsStillResolvedByItsDisplayName()
    {
        Catalogue(new GameSetting { Id = "ow-id", Name = "Overwatch" });

        Assert.Equal("ow-id", _host.ResolveDetectedGameId("Overwatch"));
    }

    // Nothing in the catalogue matches: the name is passed through, which is what a manual start for
    // an unlisted game already does.
    [Fact]
    public void AnUnlistedProcess_IsPassedThrough()
    {
        Catalogue(new GameSetting { Id = "ow-id", Name = "Overwatch" });

        Assert.Equal("doom", _host.ResolveDetectedGameId("doom"));
    }

    // ---- game capture ----

    [Fact]
    public void GameCapture_HooksTheExecutable_NotTheDisplayName()
    {
        Catalogue(new GameSetting { Id = "cs2-id", Name = "Counter-Strike 2", Executable = "cs2.exe" });

        // Extension-free: the caller appends the platform's own, and "cs2.exe.exe" hooks nothing.
        Assert.Equal("cs2", _host.GameCaptureName("cs2-id"));
    }

    [Fact]
    public void GameCapture_FallsBackToTheDisplayName_WhenNoExecutableIsSet()
    {
        Catalogue(new GameSetting { Id = "ow-id", Name = "Overwatch" });

        Assert.Equal("Overwatch", _host.GameCaptureName("ow-id"));
    }

    [Fact]
    public void GameCapture_ForAnUnlistedGame_UsesTheIdItself()
    {
        Catalogue(new GameSetting { Id = "ow-id", Name = "Overwatch" });

        Assert.Equal("doom", _host.GameCaptureName("doom"));
    }
}

// `_detectorGameNames` was declared and read by PushState but never populated, so `game.detected` on
// every state push was permanently false — the UI could never distinguish a game the watcher found
// from one started by hand. WireAutoStart needs a real recorder, so the id collection is pulled out
// where a test can reach it.
public class WatchableGameIdTests
{
    [Fact]
    public void EveryCatalogueEntryWithAnExecutable_IsWatched()
    {
        var ids = AppHost.WatchableGameIds(
        [
            new GameInfo { Id = "cs2", Name = "Counter-Strike 2", Executable = "cs2" },
            new GameInfo { Id = "ow", Name = "Overwatch" },
        ]);

        Assert.Equal(["cs2", "ow"], ids);
    }

    // An entry the watcher could never report must not claim to be watched.
    [Fact]
    public void AnEntryWithNothingToWatchOn_IsNot()
    {
        var ids = AppHost.WatchableGameIds(
        [
            new GameInfo { Id = "ok", Name = "Fine" },
            new GameInfo { Id = "no-name", Name = "", Executable = "" },
            new GameInfo { Id = "", Name = "No id" },
        ]);

        Assert.Equal(["ok"], ids);
    }

    [Fact]
    public void ARepeatedId_IsListedOnce()
    {
        var ids = AppHost.WatchableGameIds(
        [
            new GameInfo { Id = "ow", Name = "Overwatch" },
            new GameInfo { Id = "OW", Name = "Overwatch again" },
        ]);

        Assert.Single(ids);
    }
}
