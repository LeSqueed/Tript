// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;
using Xunit;

namespace Tript.Settings.Tests;

// GameSetting.Name used to be both the display name and the process name, so a game whose window
// title differs from its executable ("Counter-Strike 2" / cs2.exe) could not be listed at all.
// Executable splits them; the fallback is what keeps every settings file written before it reading
// exactly as it did.
public class GameExecutableTests
{
    [Fact]
    public void NoExecutable_FallsBackToTheDisplayName()
    {
        var game = new GameSetting { Id = "ow", Name = "Overwatch" };

        Assert.Equal("Overwatch", game.EffectiveExecutable);
    }

    [Fact]
    public void AnExecutable_WinsOverTheDisplayName()
    {
        var game = new GameSetting { Id = "cs2", Name = "Counter-Strike 2", Executable = "cs2.exe" };

        Assert.Equal("cs2.exe", game.EffectiveExecutable);
        Assert.Equal("Counter-Strike 2", game.Name);
    }

    // A blank value is a user who cleared the field, not a user who set it to nothing.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankExecutable_IsTreatedAsAbsent(string executable)
    {
        var game = new GameSetting { Id = "ow", Name = "Overwatch", Executable = executable };

        Assert.Equal("Overwatch", game.EffectiveExecutable);
    }

    // A settings file written before the field existed has no `executable` key. It must deserialize
    // to the old behaviour rather than to an empty executable that matches no process.
    [Fact]
    public void ASettingsFileWithoutTheKey_KeepsTheOldBehaviour()
    {
        const string json = """
        {
          "game": {
            "gameList": [ { "id": "ow", "name": "Overwatch" } ]
          }
        }
        """;

        var settings = SettingsSerialization.Deserialize(json);

        var game = Assert.Single(settings!.Game.GameList);
        Assert.Null(game.Executable);
        Assert.Equal("Overwatch", game.EffectiveExecutable);
    }

    [Fact]
    public void TheKeyIsReadAndWrittenAsCamelCaseExecutable()
    {
        const string json = """
        {
          "game": {
            "gameList": [ { "id": "cs2", "name": "Counter-Strike 2", "executable": "cs2.exe" } ]
          }
        }
        """;

        var settings = SettingsSerialization.Deserialize(json);
        Assert.Equal("cs2.exe", Assert.Single(settings!.Game.GameList).Executable);

        using var document = JsonDocument.Parse(SettingsSerialization.Serialize(settings));
        var entry = document.RootElement.GetProperty("game").GetProperty("gameList")[0];
        Assert.Equal("cs2.exe", entry.GetProperty("executable").GetString());

        // EffectiveExecutable is computed, so it must not appear in the file as a second, stale copy.
        Assert.False(entry.TryGetProperty("effectiveExecutable", out _));
    }

    // The default entry a fresh install ships with has no executable, and the detector has to keep
    // watching for Overwatch on first launch.
    [Fact]
    public void TheDefaultGameListEntry_StillResolvesAnExecutable()
    {
        var game = Assert.Single(new GameSettings().GameList);

        Assert.Null(game.Executable);
        Assert.Equal("Overwatch", game.EffectiveExecutable);
    }
}
