// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

// Renaming a piece of content. A recording's title lives on its RecordingMetadata record; a clip has
// no such record and keeps its title in ClipTitleStore. RenameContent wrote a RecordingMetadata
// record whatever it was handed, so a renamed clip got a record nothing ever reads back — the
// library kept showing the file name.
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
            new RecordingSessionTracker());
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
    public void RenamingAClip_WritesTheRecordTheLibraryReads()
    {
        WriteFile("clips/session-1-clip-a.mp4");

        _host.RenameContent(new RenameContentParameters
        {
            ContentType = "clip",
            FileName = "clips/session-1-clip-a.mp4",
            Title = "The clutch",
        });

        Assert.Equal("The clutch",
            new ClipTitleStore(Path.Combine(_contentRoot, "metadata")).Load("session-1-clip-a.mp4"));
    }

    // The wire's contentType is advisory — it defaults to "recording" whether or not the caller meant
    // it, and the read side classifies by path. Where the title lands has to follow the read side.
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

    // A clip's record also carries its measured duration; a rename must read-modify-write, not
    // replace.
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

    // The recording path is unchanged.
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

    // A recording's record carries its game and bookmarks; renaming must not trade them for a title.
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
