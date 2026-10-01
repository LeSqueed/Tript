// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.App.Tests;

public sealed class LibraryToolsTests
{
    private static readonly (string Ffmpeg, string Ffprobe) Found = ("ffmpeg.exe", "ffprobe.exe");

    [Fact]
    public void ASlowFirstProbe_DoesNotDisableFfmpegForTheRestOfTheSession()
    {
        long now = 0;
        var attempts = 0;
        var tools = new LibraryTools(
            () => ++attempts == 1
                ? throw new FfmpegNotFoundException("ffmpeg did not respond to -version within 30s.")
                : Found,
            () => now);

        Assert.Null(tools.Value);
        Assert.Equal("ffmpeg did not respond to -version within 30s.", tools.Failure);

        now += (long)LibraryTools.RetryAfter.TotalMilliseconds;

        Assert.Equal(Found, tools.Value);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void AFailure_IsNotRetriedOnEveryUse()
    {
        long now = 0;
        var attempts = 0;
        var tools = new LibraryTools(
            () =>
            {
                attempts++;
                throw new FfmpegNotFoundException("missing");
            },
            () => now);

        Assert.Null(tools.Value);
        now += (long)LibraryTools.RetryAfter.TotalMilliseconds - 1;
        Assert.Null(tools.Value);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void OnceFound_TheToolsAreNotLocatedAgain()
    {
        var attempts = 0;
        var tools = new LibraryTools(() => { attempts++; return Found; }, () => 0);

        Assert.Equal(Found, tools.Value);
        Assert.Equal(Found, tools.Value);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void AnExtractorThatIsNotAvailableYet_IsAskedForAgain_AndKeptOnceItIs()
    {
        var calls = 0;
        var kept = new KeptOnceAvailable<string>(() => ++calls == 1 ? null : "extractor");

        Assert.Null(kept.Value);
        Assert.Equal("extractor", kept.Value);
        Assert.Equal("extractor", kept.Value);

        Assert.Equal(2, calls);
    }
}
