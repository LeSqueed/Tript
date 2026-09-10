// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class GameCatalogueTests : IDisposable
{
    private const string OverwatchId = "57ZZVAZ0PJK8VQGPKB728QE57C";

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

    [Fact]
    public void LegacyBuiltinId_IsMigratedAndPersisted()
    {
        var root = Path.Combine(_contentRoot, "legacy");
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        var store = new SettingsStore(new SettingsFileProvider(settingsPath));
        store.Load().Game.GameList =
        [
            new GameSetting { Id = "Overwatch", Name = "Overwatch" },
        ];
        store.Save();

        using var host = new AppHost(new AppOptions
        {
            ContentRoot = root,
            SettingsPath = settingsPath,
            WebRoot = root,
            FakeRecorder = true,
        }, store, runtime: null, new RecordingSessionTracker());

        Assert.Equal(OverwatchId, Assert.Single(host.GameList).Id);
        var persisted = SettingsSerialization.Deserialize(File.ReadAllText(settingsPath));
        Assert.Equal(OverwatchId, Assert.Single(persisted!.Game.GameList).Id);
    }

    [Fact]
    public void AnEmptyCatalogue_IsNotRebuiltByReadingIt()
    {
        Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            game = new { gameList = Array.Empty<object>() },
        })));
        Assert.Equal(OverwatchId, Assert.Single(_host.GameList).Id);

        _store.Load().Game.GameList.Add(new GameSetting { Id = "Doom", Name = "Doom" });
        Assert.Equal(OverwatchId, Assert.Single(_host.GameList).Id);
        Assert.Equal(OverwatchId, Assert.Single(_host.GameList).Id);

        _host.ReloadGameList();
        Assert.Equal([OverwatchId, "Doom"], _host.GameList.Select(game => game.Id));
    }

    [Fact]
    public void ASettingsChange_ReloadsTheCatalogue()
    {
        Assert.Equal(OverwatchId, Assert.Single(_host.GameList).Id);
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

        Assert.Equal([OverwatchId, "custom-doom", "custom-quake"], _host.GameList.Select(game => game.Id));
        Assert.Equal("Doom", _host.GameList.First(game => game.Id == "custom-doom").Name);
        Assert.Equal(doom, _host.GameList.First(game => game.Id == "custom-doom").ExecutablePath);
    }

    [Fact]
    public void AReload_DoesNotMutateTheListAReaderIsAlreadyHolding()
    {
        var held = _host.GameList;
        Assert.Single(held);

        Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            game = new { gameList = Array.Empty<object>() },
        })));

        Assert.Single(_host.GameList);
        Assert.Single(held);
        Assert.NotSame(held, _host.GameList);
    }
}
