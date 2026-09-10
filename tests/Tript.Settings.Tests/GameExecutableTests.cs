// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;
using Xunit;

namespace Tript.Settings.Tests;

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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankExecutable_IsTreatedAsAbsent(string executable)
    {
        var game = new GameSetting { Id = "ow", Name = "Overwatch", Executable = executable };

        Assert.Equal("Overwatch", game.EffectiveExecutable);
    }

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

        Assert.False(entry.TryGetProperty("effectiveExecutable", out _));
    }

    [Fact]
    public void TheDefaultGameListEntry_StillResolvesAnExecutable()
    {
        var game = Assert.Single(new GameSettings().GameList);

        Assert.Null(game.Executable);
        Assert.Equal("Overwatch", game.EffectiveExecutable);
    }
}
