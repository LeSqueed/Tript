// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Linq;
using System.Text.Json;
using Tript.Core;
using Xunit;

namespace Tript.Settings.Tests;

public class BookmarkMetadataTests
{
    [Fact]
    public void BookmarkVocabulary_HasTheFiveMembersInOrder()
    {
        Assert.Equal(
            new[] { BookmarkType.Manual, BookmarkType.Kill, BookmarkType.Goal, BookmarkType.Assist, BookmarkType.Death },
            Enum.GetValues<BookmarkType>());
    }

    [Fact]
    public void HighlightMarking_DistinguishesPositiveTypesFromManualAndDeaths()
    {
        Assert.True(BookmarkType.Kill.IsIncludedInHighlights());
        Assert.True(BookmarkType.Goal.IsIncludedInHighlights());

        Assert.True(BookmarkType.Assist.IsIncludedInHighlights());

        Assert.False(BookmarkType.Manual.IsIncludedInHighlights());
        Assert.False(BookmarkType.Death.IsIncludedInHighlights());
    }

    [Fact]
    public void RecordingMetadata_RoundTripsThroughJson()
    {
        var metadata = new RecordingMetadata
        {
            Game = "Overwatch",
            ContentType = ContentType.Recording,
            StartTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local),
            AudioTracks =
            {
                new AudioTrackLayout
                {
                    Index = 1,
                    Name = "Game",
                    Sources = { new SourceOnTrack { Name = "Game audio", Volume = 1.0f } },
                },
            },
            Bookmarks =
            {
                new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(12) },
                new Bookmark { Type = BookmarkType.Death, Time = TimeSpan.FromSeconds(42) },
            },
        };

        var json = JsonSerializer.Serialize(metadata, SettingsSerialization.Options);
        var roundTripped = JsonSerializer.Deserialize<RecordingMetadata>(json, SettingsSerialization.Options)!;

        Assert.Equal("Overwatch", roundTripped.Game);
        Assert.Equal(ContentType.Recording, roundTripped.ContentType);
        var track = Assert.Single(roundTripped.AudioTracks);
        Assert.Equal(1, track.Index);
        Assert.Equal("Game audio", Assert.Single(track.Sources).Name);
        Assert.Equal(2, roundTripped.Bookmarks.Count);
        Assert.Equal(BookmarkType.Kill, roundTripped.Bookmarks[0].Type);
        Assert.Equal(TimeSpan.FromSeconds(12), roundTripped.Bookmarks[0].Time);
    }

    [Fact]
    public void RecordingMetadata_SerializesBookmarkTypeAsMemberName()
    {
        var metadata = new RecordingMetadata
        {
            Bookmarks = { new Bookmark { Type = BookmarkType.Goal, Time = TimeSpan.Zero } },
        };

        var json = JsonSerializer.Serialize(metadata, SettingsSerialization.Options);

        Assert.Contains("\"type\": \"Goal\"", json);
        Assert.DoesNotContain("\"type\": 2", json);
    }

    [Fact]
    public void RecordingMetadata_UnknownBookmarkType_DoesNotFailTheFile()
    {
        var json = """{"game":"Overwatch","bookmarks":[{"type":"HandMade","time":"00:00:10"},{"type":"Kill","time":"00:00:20"}]}""";

        var metadata = JsonSerializer.Deserialize<RecordingMetadata>(json, SettingsSerialization.Options)!;

        Assert.Equal(BookmarkType.Manual, metadata.Bookmarks[0].Type);
        Assert.Equal(BookmarkType.Kill, metadata.Bookmarks[1].Type);
        Assert.Equal(2, metadata.Bookmarks.Count);
    }

    [Fact]
    public void RecordingMetadata_UnknownContentType_FallsBackToRecording()
    {
        var json = """{"contentType":"Broadcast"}""";

        var metadata = JsonSerializer.Deserialize<RecordingMetadata>(json, SettingsSerialization.Options)!;

        Assert.Equal(ContentType.Recording, metadata.ContentType);
    }

    [Fact]
    public void RecordingMetadata_StartTimeWithAnOffset_RoundTripsAsTheSameInstant()
    {
        var json = """
            {
              "videoPath": "sessions/session-20260817-152046741.mp4",
              "game": "Overwatch",
              "contentType": "Recording",
              "startTime": "2026-08-17T15:20:46.7558115+02:00"
            }
            """;

        var metadata = JsonSerializer.Deserialize<RecordingMetadata>(json, SettingsSerialization.Options)!;

        Assert.Equal("Overwatch", metadata.Game);
        Assert.Equal("sessions/session-20260817-152046741.mp4", metadata.VideoPath);
        var expected = new DateTimeOffset(2026, 8, 17, 15, 20, 46, TimeSpan.FromHours(2)).AddTicks(7558115);
        Assert.Equal(expected, new DateTimeOffset(metadata.StartTime));

        var again = JsonSerializer.Serialize(metadata, SettingsSerialization.Options);
        Assert.Equal(expected,
            new DateTimeOffset(JsonSerializer.Deserialize<RecordingMetadata>(again,
                SettingsSerialization.Options)!.StartTime));
    }

    [Fact]
    public void RecordingMetadata_ANearlyRightRecord_StillLoads()
    {
        Assert.Equal("Overwatch", Load("""{"videoPath":"a.mp4","game":"Overwatch",}""").Game);
        Assert.Equal("Overwatch", Load("{\n// the game this belongs to\n\"videoPath\":\"a.mp4\",\"game\":\"Overwatch\"}").Game);
        Assert.Equal(9.13, Load("""{"videoPath":"a.mp4","game":"Overwatch","durationSeconds":"9.13"}""").DurationSeconds);

        var pascal = Load("""{"VideoPath":"a.mp4","Game":"Overwatch"}""");
        Assert.Equal("a.mp4", pascal.VideoPath);
        Assert.Equal("Overwatch", pascal.Game);

        static RecordingMetadata Load(string json) =>
            JsonSerializer.Deserialize<RecordingMetadata>(json, SettingsSerialization.Options)!;
    }

    [Fact]
    public void RecordingMetadata_AnUnreadableStartTime_DoesNotFailTheRecord()
    {
        var epoch = JsonSerializer.Deserialize<RecordingMetadata>(
            """{"game":"Overwatch","startTime":1786972846}""", SettingsSerialization.Options)!;
        Assert.Equal("Overwatch", epoch.Game);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786972846), new DateTimeOffset(epoch.StartTime));

        foreach (var unreadable in new[] { "\"\"", "\"17/08/2026 15:20:46\"", "null", "{\"y\":2026}" })
        {
            var metadata = JsonSerializer.Deserialize<RecordingMetadata>(
                $"{{\"game\":\"Overwatch\",\"startTime\":{unreadable},\"title\":\"Ranked win\"}}",
                SettingsSerialization.Options)!;

            Assert.Equal("Overwatch", metadata.Game);
            Assert.Equal("Ranked win", metadata.Title);
            Assert.Equal(default, metadata.StartTime);
        }
    }
}
