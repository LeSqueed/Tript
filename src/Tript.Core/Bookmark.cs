// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using System.Text.Json.Serialization;

namespace Tript.Core;

[AttributeUsage(AttributeTargets.Field)]
public sealed class IncludeInHighlightsAttribute : Attribute
{
}

public enum BookmarkType
{
    Manual,

    [IncludeInHighlights]
    Kill,

    [IncludeInHighlights]
    Goal,

    [IncludeInHighlights]
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

    [JsonConverter(typeof(BookmarkTypeConverter))]
    public BookmarkType Type { get; set; }

    public string? Subtype { get; set; }

    public TimeSpan Time { get; set; }

    public int? AiRating { get; set; }

    public bool? IsAutomaticClipCandidate { get; set; }
}
