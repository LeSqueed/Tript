// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.IO;
using Xunit;
using Tript.TestSupport;

namespace Tript.Settings.Tests;

public class RecordingLocationsTests : IDisposable
{
    private readonly string? _priorXdgVideos;

    public RecordingLocationsTests()
    {
        _priorXdgVideos = Environment.GetEnvironmentVariable("XDG_VIDEOS_DIR");
    }

    public void Dispose()
    {
        if (_priorXdgVideos is null)
            Environment.SetEnvironmentVariable("XDG_VIDEOS_DIR", null);
        else
            Environment.SetEnvironmentVariable("XDG_VIDEOS_DIR", _priorXdgVideos);
    }

    [Fact]
    public void DefaultDirectory_IsNeverEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(RecordingLocations.DefaultDirectory()));
    }

    [Fact]
    public void DefaultDirectory_EndsWithTript()
    {
        var directory = RecordingLocations.DefaultDirectory();
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        Assert.Equal("Tript", leaf);
    }

    [LinuxFact]
    public void DefaultDirectory_HonoursXdgVideosDir()
    {
        var xdg = Path.Combine(Path.GetTempPath(), "tript-test-xdg-videos");
        Environment.SetEnvironmentVariable("XDG_VIDEOS_DIR", xdg);
        try
        {
            var directory = RecordingLocations.DefaultDirectory();
            Assert.StartsWith(xdg + Path.DirectorySeparatorChar, directory);
            Assert.EndsWith(Path.Combine("Tript"), directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_VIDEOS_DIR", null);
        }
    }
}
