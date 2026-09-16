// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Tript.App.Models;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class ContentCatalogueBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tript-app-tests",
        nameof(ContentCatalogueBuilderTests), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _root + "-data" })
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void OnlyTheRootTrashFolder_IsLeftOut()
    {
        Write("sessions/kept.mp4");
        Write(".trash/entry/files/sessions/deleted.mp4");
        Write("Valorant/.trash/nested.mp4");
        Write("sessions/notes.txt");

        var paths = Build().Select(item => item.FilePath).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["Valorant/.trash/nested.mp4", "sessions/kept.mp4"], paths);
    }

    [Fact]
    public void ExtensionMatching_FollowsThePlatformCaseRule()
    {
        Write("sessions/upper.MP4");

        var listed = Build().Any(item => item.FileName == "upper.MP4");

        Assert.Equal(OperatingSystem.IsWindows(), listed);
    }

    [Fact]
    public void AClip_InheritsTheGameOfItsRecording_AndItIsBackfilled()
    {
        Write("sessions/session-1.mp4");
        Write("clips/session-1-abc.mp4");
        var metadataRoot = ContentLayout.MetadataRoot(_root);
        Assert.True(new RecordingMetadataStore(metadataRoot).Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-1.mp4",
            Game = "Old name",
            GameId = "game-1",
        }));
        var clipTitles = new ClipTitleStore(metadataRoot);
        Assert.True(clipTitles.Save("session-1-abc.mp4", "My clip"));
        var backfills = new List<LibraryBackfill>();
        var games = new LibraryGames([new GameInfo { Id = "game-1", Name = "Valorant" }], Aliases());

        var items = Build(games, backfills);

        var clip = Assert.Single(items, item => item.ContentType == "clip");
        Assert.Equal("My clip", clip.Title);
        Assert.Equal("game-1", clip.GameId);
        Assert.Equal("Valorant", clip.Game);
        var recording = Assert.Single(items, item => item.ContentType == "recording");
        Assert.Equal("Valorant", recording.Game);
        Assert.Equal(new LibraryBackfill.ClipGame("session-1-abc.mp4", "Old name", "game-1"), Assert.Single(backfills));
    }

    [Fact]
    public void AMissingRoot_ListsNothing()
    {
        Assert.Empty(Build());
    }

    private List<ContentItem> Build(LibraryGames? games = null, List<LibraryBackfill>? backfills = null)
    {
        var metadataRoot = ContentLayout.MetadataRoot(_root);
        return new ContentCatalogue(_root, new RecordingMetadataStore(metadataRoot), new ClipTitleStore(metadataRoot),
                new LibraryProbe(() => null), games ?? new LibraryGames([], Aliases()),
                backfill => backfills?.Add(backfill))
            .Build(new LibraryState(null, null, [], null));
    }

    private GameIdAliasStore Aliases() => new(Path.Combine(_root + "-data", "game-id-aliases.json"));

    private void Write(string wirePath)
    {
        var path = Path.Combine(_root, wirePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "video");
    }
}
