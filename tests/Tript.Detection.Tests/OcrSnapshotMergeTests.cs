// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Collections.Generic;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

// The object loop folds in an OCR snapshot only while it is fresh.
public class OcrSnapshotMergeTests
{
    private static List<OcrMatch> OneMatch() =>
        [new OcrMatch { EventId = 1, Text = "ELIMINATED AMON", Confidence = 0.9f }];

    [Fact]
    public void FreshMatches_WithinMaxAge_AreReturned()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var matches = OneMatch();

        var result = VisualEventDetector.FreshMatches(matches, now.AddMilliseconds(-1000), now, 2500);

        Assert.Same(matches, result);
    }

    [Fact]
    public void FreshMatches_OlderThanMaxAge_AreDropped()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var result = VisualEventDetector.FreshMatches(OneMatch(), now.AddMilliseconds(-4000), now, 2500);

        Assert.Empty(result);
    }

    [Fact]
    public void FreshMatches_WithNoSnapshot_IsEmpty()
    {
        var result = VisualEventDetector.FreshMatches(null, default, DateTime.UtcNow, 2500);

        Assert.Empty(result);
    }
}
