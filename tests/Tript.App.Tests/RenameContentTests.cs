// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class RenameContentTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly AppHost _host;

    public RenameContentTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(RenameContentTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        var settingsPath = Path.Combine(_contentRoot, "settings.json");

        _host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = settingsPath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, new SettingsStore(new SettingsFileProvider(settingsPath)), runtime: null,
            new RecordingSessionTracker(),
            storageProbe: AmpleStorage.Probe);
    }

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RenamingAClip_ShowsTheNewTitleInTheLibrary()
    {
        WriteFile("clips/session-1-clip-a.mp4");

        _host.RenameContent(new RenameContentParameters
        {
            ContentType = "clip",
            FileName = "clips/session-1-clip-a.mp4",
            Title = "The clutch",
        });

        var item = Assert.Single(_host.ListContent());
        Assert.Equal("clip", item.ContentType);
        Assert.Equal("The clutch", item.Title);
    }

    [Fact]
    public void RenamingAClip_WithTheDefaultContentType_StillLandsInTheClipRecord()
    {
        WriteFile("clips/session-1-clip-a.mp4");

        _host.RenameContent(new RenameContentParameters
        {
            FileName = "clips/session-1-clip-a.mp4",
            Title = "The clutch",
        });

        Assert.Equal("The clutch", Assert.Single(_host.ListContent()).Title);
    }

    [Fact]
    public void RenamingAClip_KeepsTheDurationAlreadyOnItsRecord()
    {
        WriteFile("clips/session-1-clip-a.mp4");
        var store = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(store.SaveDuration("session-1-clip-a.mp4", 12.5));

        _host.RenameContent(new RenameContentParameters
        {
            ContentType = "clip",
            FileName = "clips/session-1-clip-a.mp4",
            Title = "The clutch",
        });

        var record = store.LoadRecord("session-1-clip-a.mp4");
        Assert.Equal("The clutch", record?.Title);
        Assert.Equal(12.5, record?.DurationSeconds);
    }

    [Fact]
    public void RenamingARecording_StillWritesItsMetadataRecord()
    {
        WriteFile("sessions/session-1.mp4");

        _host.RenameContent(new RenameContentParameters
        {
            ContentType = "recording",
            FileName = "sessions/session-1.mp4",
            Title = "The clutch",
        });

        Assert.Equal("The clutch",
            new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata")).Load("session-1.mp4")?.Title);
        Assert.Equal("The clutch", Assert.Single(_host.ListContent()).Title);
    }

    [Fact]
    public void RenamingARecording_KeepsItsGameAndBookmarks()
    {
        WriteFile("sessions/session-1.mp4");
        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-1.mp4",
            Game = "Overwatch",
            Bookmarks = { new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(12) } },
        });

        _host.RenameContent(new RenameContentParameters
        {
            ContentType = "recording",
            FileName = "sessions/session-1.mp4",
            Title = "The clutch",
        });

        var record = store.Load("session-1.mp4");
        Assert.Equal("The clutch", record?.Title);
        Assert.Equal("Overwatch", record?.Game);
        Assert.Single(record!.Bookmarks);
    }

    private void WriteFile(string relativePath)
    {
        var path = Path.Combine(_contentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "video");
    }
}
