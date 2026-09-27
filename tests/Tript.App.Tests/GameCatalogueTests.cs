// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.App.Models;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class GameCatalogueTests : IDisposable
{
    private const string OverwatchId = "5JWDDE307Z5127JK7KM4YCB1XW";

    private readonly string _contentRoot;
    private readonly SettingsStore _store;
    private readonly AppHost _host;

    public GameCatalogueTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(GameCatalogueTests),
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
            storageProbe: AmpleStorage.Probe,
            gameIdAliases: new GameIdAliasStore(Path.Combine(_contentRoot, "game-id-aliases.json")));
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

    [Fact]
    public void AFreshInstall_StartsWithAnEmptyGameList()
    {
        Assert.Empty(_host.GameList);
        Assert.Empty(new Settings.Settings().Game.GameList);
    }

    [Theory]
    [InlineData("Overwatch")]
    [InlineData("57ZZVAZ0PJK8VQGPKB728QE57C")]
    public void AnUntouchedLegacyOverwatchEntry_IsDroppedAndPersisted(string legacyId)
    {
        var (host, settingsPath) = HostWithGames(new GameSetting { Id = legacyId, Name = "Overwatch" });
        using (host)
        {
            Assert.Empty(host.GameList);
            Assert.Empty(SettingsSerialization.Deserialize(File.ReadAllText(settingsPath))!.Game.GameList);
        }
    }

    [Theory]
    [InlineData("Overwatch")]
    [InlineData("57ZZVAZ0PJK8VQGPKB728QE57C")]
    public void ALegacyOverwatchEntryWithOverrides_KeepsThemUnderTheResolvedId(string legacyId)
    {
        var (host, settingsPath) = HostWithGames(new GameSetting
        {
            Id = legacyId,
            Name = "Overwatch",
            AutoRecordOverride = false,
        });
        using (host)
        {
            var game = Assert.Single(host.GameList);
            Assert.Equal(OverwatchId, game.Id);
            Assert.Null(game.ExecutablePath);
            var persisted = Assert.Single(
                SettingsSerialization.Deserialize(File.ReadAllText(settingsPath))!.Game.GameList);
            Assert.Equal(OverwatchId, persisted.Id);
            Assert.False(persisted.AutoRecordOverride);
        }
    }

    [Fact]
    public void AnOverwatchEntryAlreadyOnTheResolvedId_IsLeftAlone()
    {
        var (host, _) = HostWithGames(new GameSetting { Id = OverwatchId, Name = "Overwatch" });
        using (host)
            Assert.Equal(OverwatchId, Assert.Single(host.GameList).Id);
    }

    [Fact]
    public void AnEmptyCatalogue_IsNotRebuiltByReadingIt()
    {
        Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            game = new { gameList = Array.Empty<object>() },
        })));
        Assert.Empty(_host.GameList);

        _store.Load().Game.GameList.Add(new GameSetting { Id = "Doom", Name = "Doom" });
        Assert.Empty(_host.GameList);

        _host.ReloadGameList();
        Assert.Equal(["Doom"], _host.GameList.Select(game => game.Id));
    }

    [Fact]
    public void ASettingsChange_ReloadsTheCatalogue()
    {
        Assert.Empty(_host.GameList);
        var doom = Path.Combine(_contentRoot, "doom.exe");
        var quake = Path.Combine(_contentRoot, "quake.exe");
        File.WriteAllText(doom, "doom");
        File.WriteAllText(quake, "quake");

        Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            game = new
            {
                gameList = new[]
                {
                    new { id = "custom-doom", name = "Doom", executablePath = doom },
                    new { id = "custom-quake", name = "Quake", executablePath = quake },
                },
            },
        })));

        Assert.Equal(["custom-doom", "custom-quake"], _host.GameList.Select(game => game.Id));
        Assert.Equal("Doom", _host.GameList.First(game => game.Id == "custom-doom").Name);
        Assert.Equal(doom, _host.GameList.First(game => game.Id == "custom-doom").ExecutablePath);
    }

    [Fact]
    public void AReload_DoesNotMutateTheListAReaderIsAlreadyHolding()
    {
        var held = _host.GameList;
        Assert.Empty(held);

        Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            game = new { gameList = new[] { new { id = OverwatchId, name = "Overwatch" } } },
        })));

        Assert.Single(_host.GameList);
        Assert.Empty(held);
        Assert.NotSame(held, _host.GameList);
    }

    private (AppHost Host, string SettingsPath) HostWithGames(params GameSetting[] games)
    {
        var root = Path.Combine(_contentRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        var store = new SettingsStore(new SettingsFileProvider(settingsPath));
        store.Load().Game.GameList = [.. games];
        store.Save();

        var host = new AppHost(new AppOptions
        {
            ContentRoot = root,
            SettingsPath = settingsPath,
            WebRoot = root,
            FakeRecorder = true,
        }, store, runtime: null, new RecordingSessionTracker(),
            storageProbe: AmpleStorage.Probe,
            gameIdAliases: new GameIdAliasStore(Path.Combine(root, "game-id-aliases.json")));
        return (host, settingsPath);
    }
}
