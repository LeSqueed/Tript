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
    public void TheProjectCatalogueCarriesTheKnownExecutable()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("Overwatch.exe", Assert.Single(_host.GameList).Executable);
    }

    [Fact]
    public void SettingsDoNotReplaceTheProjectCatalogueIdentity()
    {
        Catalogue(new GameSetting { Id = "cs2", Name = "Counter-Strike 2", Executable = "cs2.exe" });

        var games = _host.GameList;
        var overwatch = Assert.Single(games, game => game.Id == "Overwatch");
        Assert.Equal("Overwatch", overwatch.Name);
        Assert.Equal("Overwatch.exe", overwatch.Executable);

        // The settings entry survives as a custom game rather than being discarded, but it never
        // replaces the packaged identity.
        var cs2 = Assert.Single(games, game => game.Id == "cs2");
        Assert.Equal("Counter-Strike 2", cs2.Name);
        Assert.Equal("cs2.exe", cs2.Executable);
        Assert.False(cs2.BuiltIn);
    }

    // The gameList push spells it `executable`, camelCase like every other field on the wire.
    [Fact]
    public void TheGameListPush_SpellsItExecutable()
    {
        Catalogue(new GameSetting { Id = "cs2", Name = "Counter-Strike 2", Executable = "cs2.exe" });

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(_host.GameList, Wire.Options));

        var entry = document.RootElement[0];
        Assert.Equal("Overwatch", entry.GetProperty("name").GetString());
        Assert.Equal("Overwatch.exe", entry.GetProperty("executable").GetString());
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

    // A custom game with an exact path still resolves through its basename route, so detection and
    // game capture agree without ever leaking the machine-specific path into the identity.
    [Fact]
    public void ACustomGameWithAnExactPath_ResolvesToItsStableId()
    {
        Catalogue(new GameSetting
        {
            Id = "custom-doom",
            Name = "Doom",
            ExecutablePath = @"C:\Games\Doom\doom.exe",
        });

        Assert.Equal("custom-doom", _host.ResolveDetectedGameId("doom"));
        // Game capture hooks the executable's basename, never the machine-specific path.
        Assert.Equal("doom", _host.GameCaptureName("custom-doom"));
    }

    // The detector strips a trailing .exe from both sides, so an entry may spell the executable
    // either way and the caller is never asked to guess which.
    [Theory]
    [InlineData("Overwatch.exe")]
    [InlineData("Overwatch")]
    [InlineData("OVERWATCH.EXE")]
    public void TheKnownExecutableResolvesToTheProjectGame(string executable)
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch", Executable = executable });

        Assert.Equal("Overwatch", _host.ResolveDetectedGameId("Overwatch"));
    }

    // The catalogue executable is explicit, so this does not depend on the settings entry carrying
    // a duplicate executable value.
    [Fact]
    public void AGameWithNoExecutable_IsStillResolvedByItsDisplayName()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("Overwatch", _host.ResolveDetectedGameId("Overwatch"));
    }

    // Nothing in the catalogue matches: the name is passed through, which is what a manual start for
    // an unlisted game already does.
    [Fact]
    public void AnUnlistedProcess_IsPassedThrough()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("doom", _host.ResolveDetectedGameId("doom"));
    }

    // ---- game capture ----

    [Fact]
    public void GameCapture_HooksTheExecutable_NotTheDisplayName()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("Overwatch", _host.GameCaptureName("Overwatch"));
    }

    [Fact]
    public void GameCapture_FallsBackToTheDisplayName_WhenNoExecutableIsSet()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("Overwatch", _host.GameCaptureName("Overwatch"));
    }

    [Fact]
    public void GameCapture_ForAnUnlistedGame_UsesTheIdItself()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("doom", _host.GameCaptureName("doom"));
    }
}
