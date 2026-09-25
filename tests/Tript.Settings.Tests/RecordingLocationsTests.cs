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

    private static Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string> Folders(
        string videos, string home, Environment.SpecialFolderOption? onlyWith = null) =>
        (folder, option) =>
        {
            if (onlyWith is { } required && option != required)
                return string.Empty;

            return folder switch
            {
                Environment.SpecialFolder.MyVideos => videos,
                Environment.SpecialFolder.UserProfile => home,
                _ => string.Empty,
            };
        };

    [Fact]
    public void DefaultDirectory_OnLinux_UsesTheVideosFolderFromUserDirs()
    {
        var directory = RecordingLocations.DefaultDirectory(windows: false,
            Folders("/home/ana/Filme", "/home/ana"), "/tmp");

        Assert.Equal(Path.Combine("/home/ana/Filme", "Tript"), directory);
    }

    [Fact]
    public void DefaultDirectory_OnLinux_AcceptsAVideosFolderThatDoesNotExistYet()
    {
        var directory = RecordingLocations.DefaultDirectory(windows: false,
            Folders("/home/ana/Filme", "/home/ana", onlyWith: Environment.SpecialFolderOption.DoNotVerify), "/tmp");

        Assert.Equal(Path.Combine("/home/ana/Filme", "Tript"), directory);
    }

    [Fact]
    public void DefaultDirectory_OnLinux_FallsBackToVideosUnderHome()
    {
        var directory = RecordingLocations.DefaultDirectory(windows: false, Folders("", "/home/ana"), "/tmp");

        Assert.Equal(Path.Combine("/home/ana", "Videos", "Tript"), directory);
    }

    [Fact]
    public void DefaultDirectory_FallsBackToTheTempFolder_WithNoHome()
    {
        Assert.Equal(Path.Combine("/tmp", "Tript"),
            RecordingLocations.DefaultDirectory(windows: false, Folders("", ""), "/tmp"));
        Assert.Equal(Path.Combine("/tmp", "Tript"),
            RecordingLocations.DefaultDirectory(windows: true, Folders("", ""), "/tmp"));
    }

    [Fact]
    public void DefaultDirectory_OnWindows_UsesMyVideos_ThenTheProfile()
    {
        Assert.Equal(Path.Combine(@"C:\Users\ana\Videos", "Tript"),
            RecordingLocations.DefaultDirectory(windows: true, Folders(@"C:\Users\ana\Videos", @"C:\Users\ana"), "/tmp"));
        Assert.Equal(Path.Combine(@"C:\Users\ana", "Tript"),
            RecordingLocations.DefaultDirectory(windows: true, Folders("", @"C:\Users\ana"), "/tmp"));
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
