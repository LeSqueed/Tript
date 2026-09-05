// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Settings.Tests;

public class RecordingModeTests
{
    [Fact]
    public void Values_PreserveLegacyOrdinalsAndAppendReplayBufferOnly()
    {
        Assert.Equal(2, (int)RecordingMode.Hybrid);
        Assert.Equal(3, (int)RecordingMode.ReplayBufferOnly);
    }

    [Theory]
    [InlineData("Buffer")]
    [InlineData("Hybrid")]
    public void Deserialize_NormalizesLegacyReplayModesToCombined(string mode)
    {
        var settings = SettingsSerialization.Deserialize($$$"""{"recording":{"mode":"{{{mode}}}"}}""");

        Assert.NotNull(settings);
        Assert.Equal(RecordingMode.SessionWithReplayBuffer, settings.Recording.Mode);
    }

    [Fact]
    public void Deserialize_PreservesReplayBufferOnlyGloballyAndPerGame()
    {
        const string json = """
            {
              "recording": { "mode": "ReplayBufferOnly" },
              "game": {
                "gameList": [
                  {
                    "id": "game",
                    "name": "Game",
                    "recordingModeOverride": { "mode": "ReplayBufferOnly" }
                  }
                ]
              }
            }
            """;

        var settings = SettingsSerialization.Deserialize(json);

        Assert.NotNull(settings);
        Assert.Equal(RecordingMode.ReplayBufferOnly, settings.Recording.Mode);
        Assert.Equal(RecordingMode.ReplayBufferOnly,
            Assert.Single(settings.Game.GameList).RecordingModeOverride?.Mode);
    }
}
