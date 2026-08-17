// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Linq;
using System.Text.Json;
using Tript.Core;
using Xunit;

namespace Tript.Settings.Tests;

// The bookmark vocabulary is a compatibility surface: five members in a fixed order, with only
// Kill and Goal marked for inclusion in automatic highlights. The on-file contract is the member
// name, and the converter tolerates unknown values by falling back to Manual. These tests pin
// that surface — including the deliberate non-obvious distinction that Death is not highlighted.
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
    public void HighlightMarking_DistinguishesKillsAndGoalsFromDeathsAndAssists()
    {
        Assert.True(BookmarkType.Kill.IsIncludedInHighlights());
        Assert.True(BookmarkType.Goal.IsIncludedInHighlights());

        // Deliberately not highlighted, against the obvious reading.
        Assert.False(BookmarkType.Manual.IsIncludedInHighlights());
        Assert.False(BookmarkType.Assist.IsIncludedInHighlights());
        Assert.False(BookmarkType.Death.IsIncludedInHighlights());
    }

    // Recording metadata serializes the bookmark vocabulary as member names, and the content
    // type via its own tolerant converter. This is the on-file contract from
    // spec/config-and-storage.md.
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

    // The metadata converter writes the member name, not the ordinal.
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

    // One hand-edited bookmark in a metadata file used to fail deserialization of the entire
    // file. The tolerant converter keeps the rest of the file readable.
    [Fact]
    public void RecordingMetadata_UnknownBookmarkType_DoesNotFailTheFile()
    {
        var json = """{"game":"Overwatch","bookmarks":[{"type":"HandMade","time":"00:00:10"},{"type":"Kill","time":"00:00:20"}]}""";

        var metadata = JsonSerializer.Deserialize<RecordingMetadata>(json, SettingsSerialization.Options)!;

        Assert.Equal(BookmarkType.Manual, metadata.Bookmarks[0].Type);
        Assert.Equal(BookmarkType.Kill, metadata.Bookmarks[1].Type);
        Assert.Equal(2, metadata.Bookmarks.Count);
    }

    // The content-type converter behaves the same way: an unknown value falls back rather than
    // failing the file.
    [Fact]
    public void RecordingMetadata_UnknownContentType_FallsBackToRecording()
    {
        var json = """{"contentType":"Broadcast"}""";

        var metadata = JsonSerializer.Deserialize<RecordingMetadata>(json, SettingsSerialization.Options)!;

        Assert.Equal(ContentType.Recording, metadata.ContentType);
    }

    // ---- how much a record may be off and still load ----

    // The form the app itself writes: StartTime comes from DateTime.Now, which serializes with the
    // local offset ("2026-08-17T15:20:46.7558115+02:00"). It reads back as the same instant. This is
    // pinned because it was suspected of being the cause of a lost record — it is not, and a "fix"
    // that changed the field's type or its written form would silently move the on-disk contract.
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

        // And writing it again keeps the instant, in the same offset form.
        var again = JsonSerializer.Serialize(metadata, SettingsSerialization.Options);
        Assert.Equal(expected,
            new DateTimeOffset(JsonSerializer.Deserialize<RecordingMetadata>(again,
                SettingsSerialization.Options)!.StartTime));
    }

    // A record does not have to be perfect to be worth reading: what is in it (a game, a title, a
    // bookmark list) cannot be recomputed, so anything unambiguous is accepted. Each case below threw
    // JsonException before, which made the record count as garbage.
    [Fact]
    public void RecordingMetadata_ANearlyRightRecord_StillLoads()
    {
        Assert.Equal("Overwatch", Load("""{"videoPath":"a.mp4","game":"Overwatch",}""").Game);
        Assert.Equal("Overwatch", Load("{\n// the game this belongs to\n\"videoPath\":\"a.mp4\",\"game\":\"Overwatch\"}").Game);
        Assert.Equal(9.13, Load("""{"videoPath":"a.mp4","game":"Overwatch","durationSeconds":"9.13"}""").DurationSeconds);

        // PascalCase members used to parse into an empty record — no exception, and silently no
        // videoPath and no game, which is worse than a failure.
        var pascal = Load("""{"VideoPath":"a.mp4","Game":"Overwatch"}""");
        Assert.Equal("a.mp4", pascal.VideoPath);
        Assert.Equal("Overwatch", pascal.Game);

        static RecordingMetadata Load(string json) =>
            JsonSerializer.Deserialize<RecordingMetadata>(json, SettingsSerialization.Options)!;
    }

    // A timestamp is the one field the library can do without — the list falls back to the video's
    // last-write time — so an unreadable one degrades to no timestamp instead of failing the record
    // and putting the game and the bookmarks at risk. Epoch seconds are read rather than dropped:
    // they name the same instant unambiguously.
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
