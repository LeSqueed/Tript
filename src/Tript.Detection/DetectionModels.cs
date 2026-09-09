// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json.Serialization;
using Tript.Core;

namespace Tript.Detection;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventType
{
    Trigger,
    Exclusion,
    Subtractor
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectionKind
{
    Object,
    Ocr
}

public sealed class OcrPatternDefinition
{
    public string LanguageTag { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public int MaximumEditDistance { get; set; } = 2;
    public double MinimumScore { get; set; } = 0.75;
}

public sealed class OcrSegmentDefinition
{
    public string Id { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = 1;
    public float Height { get; set; } = 1;
}

// Defaults sized for the host's ~1 s cycle; per-event events.json overrides them.
public sealed class OcrTrackingDefinition
{
    public int ConfirmationFrames { get; set; } = 2;
    public int MinimumStableMilliseconds { get; set; } = 600;
    public int ExpireAfterMissingMilliseconds { get; set; } = 1200;
    public double MaximumTextDistance { get; set; } = 0.2;
    public float MinimumBoundsIou { get; set; } = 0.3f;
}

public sealed class OcrEventDefinition
{
    public List<OcrPatternDefinition> Patterns { get; set; } = [];
    public List<OcrSegmentDefinition> Segments { get; set; } = [];
    public float MinimumConfidence { get; set; } = 0.5f;
    public OcrTrackingDefinition Tracking { get; set; } = new();
}

public class EventDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public EventType Type { get; set; }
    public DetectionKind DetectionKind { get; set; }
    public int ClassId { get; set; }
    public OcrEventDefinition? Ocr { get; set; }
    public int? SubtractsEventId { get; set; }
    // Without the converter these read as numbers and events.json's "Kill"/"Assist" throw.
    [JsonConverter(typeof(BookmarkTypeConverter))]
    public BookmarkType? BookmarkType { get; set; }

    // Whether bookmarks from this event should produce an automated session clip. This is kept on
    // the event definition rather than inferred from BookmarkType: a game may use the same bookmark
    // vocabulary for positive and negative moments.
    public bool IncludeInAutoClips { get; set; }

    public float? ScreenRegionX { get; set; }
    public float? ScreenRegionY { get; set; }
    public float? ScreenRegionW { get; set; }
    public float? ScreenRegionH { get; set; }
    public int? RegionGroupId { get; set; }
    public bool FixedPosition { get; set; }
    public double? FixedLabelCenterX { get; set; }
    public double? FixedLabelCenterY { get; set; }
    public double? FixedLabelWidth { get; set; }
    public double? FixedLabelHeight { get; set; }
}

public class DetectionResult
{
    public int ClassId { get; set; }
    public float Confidence { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public DateTime Timestamp { get; set; }
}

public sealed class OcrMatch
{
    public int EventId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string NormalizedText { get; set; } = string.Empty;
    public string LanguageTag { get; set; } = string.Empty;
    public string SegmentId { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

public sealed class DetectionBatch
{
    public DateTime FrameTimestamp { get; set; }
    public List<DetectionResult> ObjectDetections { get; set; } = [];
    public List<OcrMatch> OcrMatches { get; set; } = [];
}

internal class RegionGroup
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
}

public sealed class RegionGroupDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public float? ScreenRegionX { get; set; }
    public float? ScreenRegionY { get; set; }
    public float? ScreenRegionW { get; set; }
    public float? ScreenRegionH { get; set; }
}
