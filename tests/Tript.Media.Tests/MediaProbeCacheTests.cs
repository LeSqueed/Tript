// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Media.Tests;

public sealed class MediaProbeCacheTests
{
    // The replay scratch file is rewritten at the same path for every live highlight. With the cache
    // keyed on path alone, the second probe returned the first file's duration and the highlight's
    // clip regions were computed against the wrong length.
    [Fact]
    public void AFileRewrittenAtTheSamePathIsProbedAgain()
    {
        var probe = new MediaProbe(MediaTestFixture.Binaries.Ffprobe);
        var path = MediaTestFixture.CreateSdrSource($"reused-{Guid.NewGuid():N}.mp4", durationSeconds: 2);

        var first = probe.Probe(path).DurationSeconds;

        var longer = MediaTestFixture.CreateSdrSource($"longer-{Guid.NewGuid():N}.mp4", durationSeconds: 5);
        File.Copy(longer, path, overwrite: true);

        var second = probe.Probe(path).DurationSeconds;

        Assert.InRange(first, 1.5, 2.5);
        Assert.InRange(second, 4.5, 5.5);
    }

    [Fact]
    public void AnUnchangedFileIsServedFromTheCache()
    {
        var probe = new MediaProbe(MediaTestFixture.Binaries.Ffprobe);
        var path = MediaTestFixture.CreateSdrSource($"unchanged-{Guid.NewGuid():N}.mp4", durationSeconds: 2);

        var first = probe.Probe(path);
        var second = probe.Probe(path);

        Assert.Same(first, second);
        Assert.Equal(1, probe.CachedCount);
    }
}
