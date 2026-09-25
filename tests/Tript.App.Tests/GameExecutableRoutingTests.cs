// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class GameExecutableRoutingTests : IDisposable
{
    private const string OverwatchId = "57ZZVAZ0PJK8VQGPKB728QE57C";

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
        }, _store, runtime: null, new RecordingSessionTracker(),
            storageProbe: AmpleStorage.Probe);
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
        var overwatch = Assert.Single(games, game => game.Id == OverwatchId);
        Assert.Equal("Overwatch", overwatch.Name);
        Assert.Equal("Overwatch.exe", overwatch.Executable);

        var cs2 = Assert.Single(games, game => game.Id == "cs2");
        Assert.Equal("Counter-Strike 2", cs2.Name);
        Assert.Equal("cs2.exe", cs2.Executable);
        Assert.False(cs2.BuiltIn);
    }

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

    [Fact]
    public void ADetectedProcess_ResolvesToTheGamesId_NotItsProcessName()
    {
        Catalogue(new GameSetting { Id = "cs2-id", Name = "Counter-Strike 2", Executable = "cs2.exe" });

        Assert.Equal("cs2-id", _host.ResolveDetectedGameId("cs2"));
    }

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

        Assert.Equal("doom", _host.GameCaptureName("custom-doom"));
    }

    [Theory]
    [InlineData("Overwatch.exe")]
    [InlineData("Overwatch")]
    [InlineData("OVERWATCH.EXE")]
    public void TheKnownExecutableResolvesToTheProjectGame(string executable)
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch", Executable = executable });

        Assert.Equal(OverwatchId, _host.ResolveDetectedGameId("Overwatch"));
    }

    [Fact]
    public void AGameWithNoExecutable_IsStillResolvedByItsDisplayName()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal(OverwatchId, _host.ResolveDetectedGameId("Overwatch"));
    }

    [Fact]
    public void AnUnlistedProcess_IsPassedThrough()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("doom", _host.ResolveDetectedGameId("doom"));
    }

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
