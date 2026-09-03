// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.IO;
using Xunit;
using Tript.TestSupport;

namespace Tript.Settings.Tests;

// The default recording directory is the one place the host and the settings UI agree on where
// recordings go when the user has not chosen a location. These tests pin the contract the rest of
// the code relies on: it is non-empty on every platform, ends with the Tript directory name, and
// honours the XDG videos variable on Linux.
public class RecordingLocationsTests : IDisposable
{
    private readonly string? _priorXdgVideos;

    public RecordingLocationsTests()
    {
        _priorXdgVideos = Environment.GetEnvironmentVariable("XDG_VIDEOS_DIR");
    }

    public void Dispose()
    {
        // Restore the environment the test found, so a parallel sibling does not see a value we
        // set. The tests below also set their own values before asserting, so the restore is a
        // belt-and-braces courtesy rather than a correctness dependency.
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

    // The default is the platform videos folder joined with "Tript" — the contract the host and
    // the UI hint both assume.
    [Fact]
    public void DefaultDirectory_EndsWithTript()
    {
        var directory = RecordingLocations.DefaultDirectory();
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        Assert.Equal("Tript", leaf);
    }

    // On Linux the XDG videos directory is honoured when the desktop sets it.
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
