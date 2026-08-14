// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using System.Text.Json.Serialization;

namespace Tript.Core;

// Whether a bookmark type earns a place in the automatic highlights is a property of the type, not
// of a rule elsewhere that has to be kept in step with this list.
[AttributeUsage(AttributeTargets.Field)]
public sealed class IncludeInHighlightsAttribute : Attribute
{
}

public enum BookmarkType
{
    // Member order is a compatibility surface: these names and positions appear in recording
    // metadata on disk.
    Manual,

    [IncludeInHighlights]
    Kill,

    [IncludeInHighlights]
    Goal,

    // Deliberately not highlighted, against the obvious reading: an assist is not the play, and a
    // death is not a moment the player wants cut into a reel.
    Assist,
    Death
}

public static class BookmarkTypeExtensions
{
    private static readonly HashSet<BookmarkType> Highlighted = ReadHighlighted();

    public static bool IsIncludedInHighlights(this BookmarkType type) => Highlighted.Contains(type);

    private static HashSet<BookmarkType> ReadHighlighted()
    {
        var highlighted = new HashSet<BookmarkType>();
        foreach (var field in typeof(BookmarkType).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetCustomAttribute<IncludeInHighlightsAttribute>() != null)
                highlighted.Add((BookmarkType)field.GetValue(null)!);
        }

        return highlighted;
    }
}

public class Bookmark
{
    public Guid Id { get; init; } = Guid.NewGuid();

    // Without the converter an unknown or malformed value fails the whole metadata file rather
    // than the one bookmark it belongs to.
    [JsonConverter(typeof(BookmarkTypeConverter))]
    public BookmarkType Type { get; set; }

    // Free-form refinement of the type — which game event produced a Kill, say. Optional because
    // most bookmarks have nothing to add to their type.
    public string? Subtype { get; set; }

    // Offset from the start of the recording, not a wall clock: the file is what it indexes into,
    // and it stays correct when the recording is moved or its start time is unknown.
    public TimeSpan Time { get; set; }

    // Assigned after the fact by whatever scores the moment, so it is absent on a bookmark that
    // has not been rated rather than zero.
    public int? AiRating { get; set; }
}
