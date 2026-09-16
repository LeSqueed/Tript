// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class DetectionCountingTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly EventDefinition Kill = new() { Id = 1, Name = "kill", Type = EventType.Trigger };

    private static readonly EventDefinition Assist = new()
    {
        Id = 2, Name = "assist", Type = EventType.Subtractor, SubtractsEventId = 1,
    };

    private static readonly EventDefinition Replay = new() { Id = 3, Name = "replay", Type = EventType.Exclusion };

    private static readonly EventDefinition Banner = new()
    {
        Id = 4, Name = "banner", Type = EventType.Trigger, DetectionKind = DetectionKind.Ocr,
        Ocr = new OcrEventDefinition
        {
            Tracking = new OcrTrackingDefinition { ConfirmationFrames = 2, MinimumStableMilliseconds = 10_000 },
        },
    };

    [Fact]
    public void ANewTrigger_IsReportedOnceUntilItDisappears()
    {
        var counter = Counter(Kill);
        var increases = new List<int>();

        counter.Count([(Box(0.1f), Kill)], [], (_, increase) => increases.Add(increase));
        counter.Count([(Box(0.1f), Kill)], [], (_, increase) => increases.Add(increase));
        counter.Count([(Box(0.1f), Kill), (Box(0.6f), Kill)], [], (_, increase) => increases.Add(increase));

        Assert.Equal([1, 1], increases);
    }

    [Fact]
    public void ASubtractor_RemovesItsTargetFromTheCount()
    {
        var counter = Counter(Kill, Assist);
        var increases = new List<int>();

        counter.Count([(Box(0.1f), Kill), (Box(0.6f), Kill), (Box(0.1f), Assist)], [],
            (_, increase) => increases.Add(increase));

        Assert.Equal([1], increases);
    }

    [Fact]
    public void AnExclusion_SuppressesTheCycle_AndRemembersWhatWasOnScreen()
    {
        var counter = Counter(Kill, Replay);
        var increases = new List<int>();

        counter.Count([(Box(0.1f), Kill), (Box(0.5f), Replay)], [], (_, increase) => increases.Add(increase));
        counter.Count([(Box(0.1f), Kill)], [], (_, increase) => increases.Add(increase));

        Assert.Empty(increases);
    }

    [Fact]
    public void OcrText_IsConfirmedAfterEnoughFrames_AndExpiresWhenGone()
    {
        var tracker = new OcrTextTracker([Kill, Banner]);

        Assert.Empty(tracker.Update([Text("DOUBLE KILL")], Origin));
        Assert.Equal([Banner], tracker.Update([Text("DOUBLE KILL")], Origin.AddMilliseconds(100)));
        Assert.Equal([Banner], tracker.Update([Text("DOUBLE KIL")], Origin.AddMilliseconds(200)));
        Assert.Empty(tracker.Update([], Origin.AddMilliseconds(2000)));
    }

    [Fact]
    public void DuplicateOcrReads_InOneFrame_CountAsOneTrack()
    {
        var tracker = new OcrTextTracker([Banner]);

        tracker.Update([Text("ACE"), Text("ACE")], Origin);
        var active = tracker.Update([Text("ACE"), Text("ACE")], Origin.AddMilliseconds(100));

        Assert.Single(active);
    }

    [Theory]
    [InlineData("kill", "kill", 0)]
    [InlineData("kill", "kil", 0.25)]
    [InlineData("abc", "xyz", 1)]
    public void TextDistance_IsNormalisedEditDistance(string left, string right, double expected)
    {
        Assert.Equal(expected, OcrTextTracker.TextDistance(left, right), 3);
    }

    private static TriggerCounter Counter(params EventDefinition[] definitions) =>
        new("game", definitions.ToDictionary(definition => definition.Id));

    private static DetectionResult Box(float x) => new()
    {
        Confidence = 0.9f, X = x, Y = 0.1f, Width = 0.1f, Height = 0.1f, Timestamp = Origin,
    };

    private static OcrMatch Text(string text) => new()
    {
        EventId = Banner.Id, Text = text, NormalizedText = text, Confidence = 0.95f,
        X = 0.4f, Y = 0.1f, Width = 0.2f, Height = 0.05f,
    };
}
