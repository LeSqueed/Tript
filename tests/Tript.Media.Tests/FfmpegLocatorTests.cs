// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

public class FfmpegLocatorTests
{
    [Fact]
    public void Locate_FindsFfmpegAndFfprobeOnPath()
    {
        var (ffmpeg, ffprobe) = new FfmpegLocator().Locate();
        Assert.False(string.IsNullOrWhiteSpace(ffmpeg));
        Assert.False(string.IsNullOrWhiteSpace(ffprobe));
        Assert.True(File.Exists(ffmpeg));
        Assert.True(File.Exists(ffprobe));
    }

    [Fact]
    public void Locate_SearchDirectoryWithBinaries_FindsThem()
    {
        var (ffmpeg, ffprobe) = new FfmpegLocator().Locate();
        var dir = Path.GetDirectoryName(ffmpeg)!;

        var found = new FfmpegLocator { SearchDirectory = dir }.Locate();
        Assert.Equal(Path.GetFullPath(ffmpeg), found.Ffmpeg);
        Assert.Equal(Path.GetFullPath(ffprobe), found.Ffprobe);
    }

    [Fact]
    public void Locate_EmptyDirectory_ThrowsClearError()
    {
        var emptyDir = Path.Combine(MediaTestFixture.ScratchRoot, "empty-bin");
        Directory.CreateDirectory(emptyDir);

        var ex = Assert.Throws<FfmpegNotFoundException>(() =>
            new FfmpegLocator { SearchDirectory = emptyDir }.Locate());
        Assert.Contains("ffmpeg", ex.Message);
    }
}
