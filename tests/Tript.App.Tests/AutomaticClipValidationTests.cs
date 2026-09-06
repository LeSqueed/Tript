// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;
using Xunit;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App.Tests;

// The rejection rules for the automatic-clip window, pinned at the validator's seam. The frontend
// UI can only move the global and per-game before/after fields, but the control socket is a trust
// boundary regardless: the effective window — each side resolved independently, a per-game override
// when set and the global value otherwise — must not end up with after below before. Equal values
// are a valid window; testing them is not testing the clamp, it is pinning what the clamp is
// allowed to leave in place.
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
        // Globals are sane; the game overrides BOTH sides into an inverted window. The failure must
        // say which game so the settings page can surface it on the right row.
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
        // Global window is 5s before / 8s after. The game raises only its before side to 10s, so
        // the effective window inverts (10 before vs 8 after) even though neither global looks bad.
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
        // Global window is 5s before / 8s after. The game lowers only its after side to 3s.
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
        // A zero-length window (before == after) is valid: the planner simply keeps nothing before
        // the bookmark and nothing after. Rejecting it would forbid the "fire on the mark" shape.
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
        // Globals sane; the default packaged game carries no override and inherits them.
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
        // A game with a sane override of its own: 2s before / 10s after, both narrower and wider
        // than the globals without ever inverting.
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

// The same rules through UpdateSettings, where the private PatchUpdatesAutomaticClipWindows gates
// them: an update is rejected only when the patch could actually have changed the window, and a
// rejected update changes nothing on disk. A patch to an unrelated page passes even when the stored
// window is already inverted, because it cannot have caused it.
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
        // The stored window is inverted, but the patch touches only the general page. The private
        // PatchUpdatesAutomaticClipWindows must not claim such a patch can change the window, or
        // this perfectly ordinary update would be refused for a window it never touched.
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
        }, store, runtime: null, new RecordingSessionTracker());
        return (store, host);
    }
}
