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

public class EventDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public EventType Type { get; set; }
    public int ClassId { get; set; }
    public int? SubtractsEventId { get; set; }
    // Without the converter these read as numbers and events.json's "Kill"/"Assist" throw.
    [JsonConverter(typeof(BookmarkTypeConverter))]
    public BookmarkType? BookmarkType { get; set; }
    public int? LifetimeMs { get; set; }
    public float? ScreenRegionX { get; set; }
    public float? ScreenRegionY { get; set; }
    public float? ScreenRegionW { get; set; }
    public float? ScreenRegionH { get; set; }
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

internal class RegionGroup
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
}
