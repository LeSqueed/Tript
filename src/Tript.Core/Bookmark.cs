// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json.Serialization;

namespace Tript.Core;

public enum BookmarkType
{
    Manual,
    Kill,
    Goal,
    Assist,
    Death,
    Play
}

public static class BookmarkTypeExtensions
{
    public static bool IsIncludedInHighlights(this BookmarkType type) =>
        type is BookmarkType.Kill or BookmarkType.Goal or BookmarkType.Assist;
}

public class Bookmark
{
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonConverter(typeof(BookmarkTypeConverter))]
    public BookmarkType Type { get; set; }

    public string? Subtype { get; set; }

    public TimeSpan Time { get; set; }

    public int? AiRating { get; set; }

    public bool? IsAutomaticClipCandidate { get; set; }
}
