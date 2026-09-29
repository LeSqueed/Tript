// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Text.Json;
using Tript.Core;
using Xunit;

namespace Tript.Detection.Tests;

public class BookmarkTypeConverterTests
{
    private static Bookmark Deserialize(string typeToken) =>
        JsonSerializer.Deserialize<Bookmark>($$"""{"Type":{{typeToken}},"Time":"00:00:10"}""")!;

    [Theory]
    [InlineData("\"Kill\"", BookmarkType.Kill)]
    [InlineData("\"goal\"", BookmarkType.Goal)]
    public void AKnownName_IsReadCaseInsensitively(string token, BookmarkType expected) =>
        Assert.Equal(expected, Deserialize(token).Type);

    [Fact]
    public void Play_RoundTripsWithItsSubtype()
    {
        var json = JsonSerializer.Serialize(new Bookmark { Type = BookmarkType.Play, Subtype = "Vehicle destroyed" });

        Assert.Contains("\"Play\"", json);
        var bookmark = JsonSerializer.Deserialize<Bookmark>(json)!;
        Assert.Equal(BookmarkType.Play, bookmark.Type);
        Assert.Equal("Vehicle destroyed", bookmark.Subtype);
    }

    [Theory]
    [InlineData("\"RemovedInAnEarlierVersion\"")]
    [InlineData("3")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("""{"name":"Kill","nested":{"a":[1,2]}}""")]
    [InlineData("""["Kill"]""")]
    public void AnUnreadableToken_FallsBackToManual_WithoutFailingTheRestOfTheObject(string token)
    {
        var bookmark = Deserialize(token);

        Assert.Equal(BookmarkType.Manual, bookmark.Type);
        Assert.Equal(TimeSpan.FromSeconds(10), bookmark.Time);
    }
}
