// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;
using Xunit;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App.Tests;

public sealed class AutomaticClipWindowValidationTests
{
    [Fact]
    public void ValidateAutomaticClipWindows_RejectsGlobalAfterBelowBefore()
    {
        var settings = new SettingsModel
        {
            Recording =
            {
                AutomaticClipBeforeSeconds = 10,
                AutomaticClipAfterSeconds = 5,
            },
        };

        Assert.False(AppHost.ValidateAutomaticClipWindows(settings, out var failure));
        Assert.Contains("after a bookmark cannot be lower than seconds before", failure);
    }

    [Fact]
    public void ValidateAutomaticClipWindows_RejectsAnInvertedFullOverrideAndNamesTheGame()
    {
        var settings = new SettingsModel();
        settings.Game.GameList.Add(new GameSetting
        {
            Id = "custom-doom",
            Name = "Doom",
            AutomaticClipOverride = new GameAutomaticClipOverride { BeforeSeconds = 30, AfterSeconds = 20 },
        });

        Assert.False(AppHost.ValidateAutomaticClipWindows(settings, out var failure));
        Assert.Contains("Doom", failure);
    }

    [Fact]
    public void ValidateAutomaticClipWindows_RejectsABeforeOnlyOverrideAboveTheGlobalAfter()
    {
        var settings = new SettingsModel();
        settings.Game.GameList.Add(new GameSetting
        {
            Id = "custom-quake",
            Name = "Quake",
            AutomaticClipOverride = new GameAutomaticClipOverride { BeforeSeconds = 10 },
        });

        Assert.False(AppHost.ValidateAutomaticClipWindows(settings, out var failure));
        Assert.Contains("Quake", failure);
    }

    [Fact]
    public void ValidateAutomaticClipWindows_RejectsAnAfterOnlyOverrideBelowTheGlobalBefore()
    {
        var settings = new SettingsModel();
        settings.Game.GameList.Add(new GameSetting
        {
            Id = "custom-ow2",
            Name = "OW2",
            AutomaticClipOverride = new GameAutomaticClipOverride { AfterSeconds = 3 },
        });

        Assert.False(AppHost.ValidateAutomaticClipWindows(settings, out var failure));
        Assert.Contains("OW2", failure);
    }

    [Fact]
    public void ValidateAutomaticClipWindows_AcceptsAnEqualWindow()
    {
        var settings = new SettingsModel
        {
            Recording =
            {
                AutomaticClipBeforeSeconds = 5,
                AutomaticClipAfterSeconds = 5,
            },
        };

        Assert.True(AppHost.ValidateAutomaticClipWindows(settings, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void ValidateAutomaticClipWindows_AcceptsValidInheritance()
    {
        var settings = new SettingsModel
        {
            Recording =
            {
                AutomaticClipBeforeSeconds = 5,
                AutomaticClipAfterSeconds = 8,
            },
        };

        Assert.True(AppHost.ValidateAutomaticClipWindows(settings, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void ValidateAutomaticClipWindows_AcceptsACorrectOverride()
    {
        var settings = new SettingsModel();
        settings.Game.GameList.Add(new GameSetting
        {
            Id = "custom-forza",
            Name = "Forza",
            AutomaticClipOverride = new GameAutomaticClipOverride { BeforeSeconds = 2, AfterSeconds = 10 },
        });

        Assert.True(AppHost.ValidateAutomaticClipWindows(settings, out var failure));
        Assert.Null(failure);
    }
}

public sealed class AutomaticClipWindowUpdateTests : IDisposable
{
    private const string OverwatchId = "57ZZVAZ0PJK8VQGPKB728QE57C";
    private const string InvertedWindowJson =
        """{"recording":{"automaticClipBeforeSeconds":10,"automaticClipAfterSeconds":5}}""";

    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public AutomaticClipWindowUpdateTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests",
            nameof(AutomaticClipWindowUpdateTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        _settingsPath = Path.Combine(_contentRoot, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ARejectedGlobalWindow_IsNotPersisted()
    {
        Seed("""{"recording":{"automaticClipBeforeSeconds":5,"automaticClipAfterSeconds":8}}""");
        var (store, host) = NewHost();
        using var scope = host;

        host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            recording = new { automaticClipAfterSeconds = 2 },
        }));

        Assert.Equal(5, store.Load().Recording.AutomaticClipBeforeSeconds);
        Assert.Equal(8, store.Load().Recording.AutomaticClipAfterSeconds);

        using var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.Equal(5, onDisk.RootElement.GetProperty("recording")
            .GetProperty("automaticClipBeforeSeconds").GetInt32());
        Assert.Equal(8, onDisk.RootElement.GetProperty("recording")
            .GetProperty("automaticClipAfterSeconds").GetInt32());
    }

    [Fact]
    public void ARejectedPerGameOverride_IsNotPersisted()
    {
        Seed("""{"game":{"gameList":[{"id":"Overwatch","name":"Overwatch"}]}}""");
        var (store, host) = NewHost();
        using var scope = host;

        host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            game = new
            {
                gameList = new[]
                {
                    new
                    {
                        id = "Overwatch",
                        name = "Overwatch",
                        automaticClipOverride = new { beforeSeconds = 30, afterSeconds = 10 },
                    },
                },
            },
        }));

        var game = Assert.Single(store.Load().Game.GameList);
        Assert.Equal("Overwatch", game.Name);
        Assert.Null(game.AutomaticClipOverride);

        using var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        var savedGame = onDisk.RootElement.GetProperty("game").GetProperty("gameList")[0];
        Assert.False(savedGame.TryGetProperty("automaticClipOverride", out _));
        Assert.Equal(OverwatchId, savedGame.GetProperty("id").GetString());
    }

    [Fact]
    public void AValidGlobalWindowPatch_IsAcceptedAndStored()
    {
        var (store, host) = NewHost();
        using var scope = host;

        host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            recording = new { automaticClipBeforeSeconds = 5, automaticClipAfterSeconds = 12 },
        }));

        Assert.Equal(5, store.Load().Recording.AutomaticClipBeforeSeconds);
        Assert.Equal(12, store.Load().Recording.AutomaticClipAfterSeconds);

        using var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.Equal(5, onDisk.RootElement.GetProperty("recording")
            .GetProperty("automaticClipBeforeSeconds").GetInt32());
        Assert.Equal(12, onDisk.RootElement.GetProperty("recording")
            .GetProperty("automaticClipAfterSeconds").GetInt32());
    }

    [Fact]
    public void AValidPerGameOverridePatch_IsAcceptedAndStored()
    {
        Seed("""{"game":{"gameList":[{"id":"Overwatch","name":"Overwatch"}]}}""");
        var (store, host) = NewHost();
        using var scope = host;

        host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            game = new
            {
                gameList = new[]
                {
                    new
                    {
                        id = "Overwatch",
                        name = "Overwatch",
                        automaticClipOverride = new { beforeSeconds = 2, afterSeconds = 10 },
                    },
                },
            },
        }));

        var game = Assert.Single(store.Load().Game.GameList);
        Assert.Equal(2, game.AutomaticClipOverride?.BeforeSeconds);
        Assert.Equal(10, game.AutomaticClipOverride?.AfterSeconds);

        using var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        var savedOverride = onDisk.RootElement.GetProperty("game").GetProperty("gameList")[0]
            .GetProperty("automaticClipOverride");
        Assert.Equal(2, savedOverride.GetProperty("beforeSeconds").GetInt32());
        Assert.Equal(10, savedOverride.GetProperty("afterSeconds").GetInt32());
    }

    [Fact]
    public void AnUnrelatedPagePatch_IsAcceptedEvenWhenTheStoredWindowIsInvalid()
    {
        Seed(InvertedWindowJson);
        var (store, host) = NewHost();
        using var scope = host;

        host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            general = new { startWithWindows = true },
        }));

        Assert.True(store.Load().General.StartWithWindows);
        Assert.Equal(10, store.Load().Recording.AutomaticClipBeforeSeconds);
        Assert.Equal(5, store.Load().Recording.AutomaticClipAfterSeconds);

        using var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.True(onDisk.RootElement.GetProperty("general").GetProperty("startWithWindows").GetBoolean());
        Assert.Equal(5, onDisk.RootElement.GetProperty("recording")
            .GetProperty("automaticClipAfterSeconds").GetInt32());
    }

    private void Seed(string json) => File.WriteAllText(_settingsPath, json);

    private (SettingsStore Store, AppHost Host) NewHost()
    {
        var store = new SettingsStore(new SettingsFileProvider(_settingsPath));
        var host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = _settingsPath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, store, runtime: null, new RecordingSessionTracker(),
            storageProbe: AmpleStorage.Probe);
        return (store, host);
    }
}
