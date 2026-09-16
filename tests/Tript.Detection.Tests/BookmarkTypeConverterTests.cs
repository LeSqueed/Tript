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

    [Fact]
    public void KnownName_RoundTrips()
    {
        Assert.Equal(BookmarkType.Kill, Deserialize("\"Kill\"").Type);
    }

    [Fact]
    public void KnownName_IsCaseInsensitive()
    {
        Assert.Equal(BookmarkType.Goal, Deserialize("\"goal\"").Type);
    }

    [Fact]
    public void UnknownName_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("\"RemovedInAnEarlierVersion\"").Type);
    }

    [Fact]
    public void NumericToken_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("3").Type);
    }

    [Fact]
    public void NumericToken_DoesNotFailTheRestOfTheObject()
    {
        var bookmark = Deserialize("3");

        Assert.Equal(BookmarkType.Manual, bookmark.Type);
        Assert.Equal(TimeSpan.FromSeconds(10), bookmark.Time);
    }

    [Fact]
    public void NullToken_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("null").Type);
    }

    [Fact]
    public void BooleanToken_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("true").Type);
    }

    [Fact]
    public void ObjectToken_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("""{"name":"Kill","nested":{"a":[1,2]}}""").Type);
    }

    [Fact]
    public void ArrayToken_FallsBackToManual()
    {
        Assert.Equal(BookmarkType.Manual, Deserialize("""["Kill"]""").Type);
    }
}
