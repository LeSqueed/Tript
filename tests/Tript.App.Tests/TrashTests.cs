// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.App.Content;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

// The trash. A delete no longer unlinks: the video and every record keyed to it move into
// <root>/.trash/<entryId>/files/<original path>, so the delete is undoable until the retention
// window closes.
public sealed class TrashTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly SettingsStore _store;
    private readonly AppHost _host;

    public TrashTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(TrashTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        var settingsPath = Path.Combine(_contentRoot, "settings.json");
        _store = new SettingsStore(new SettingsFileProvider(settingsPath));

        _host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = settingsPath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, _store, runtime: null, new RecordingSessionTracker());
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

    // ---- delete -> trash ----

    [Fact]
    public void DeleteContent_MovesTheVideoAndEveryRecordKeyedToIt()
    {
        WriteSession("session-1.mp4", "Overwatch", "The clutch");
        var thumbnail = WriteThumbnail("session-1.mp4");

        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });

        Assert.False(File.Exists(Path.Combine(_contentRoot, "sessions", "session-1.mp4")),
            "the video must leave the sessions directory");
        Assert.False(File.Exists(MetadataPath("session-1.mp4")), "the metadata record must move with its video");
        Assert.False(File.Exists(thumbnail), "the cached thumbnail must move with its video");

        var entry = Assert.Single(_host.TrashEntries());
        Assert.Equal("session-1.mp4", entry.FileName);
        Assert.Equal("recording", entry.ContentType);
        Assert.Equal("The clutch", entry.Title);
        Assert.Equal("Overwatch", entry.Game);
        Assert.True(entry.FileSizeBytes > 0);
        Assert.True(entry.DeletedAt > 0);
        Assert.Equal(entry.DeletedAt + 24 * 3600, entry.PurgeAt);

        // The mirror under files/ is what a restore reads: everything sits at the path it came from.
        var files = Path.Combine(EntryDirectory(entry.Id), "files");
        Assert.True(File.Exists(Path.Combine(files, "sessions", "session-1.mp4")));
        Assert.True(File.Exists(Path.Combine(files, "metadata", "session-1.mp4.metadata.json")));
        Assert.True(File.Exists(Path.Combine(files, "metadata", "thumbnails", "session-1.mp4.jpg")));

        // And the library no longer lists it.
        Assert.Empty(_host.ListContent());
    }

    [Fact]
    public void DeleteContent_Permanent_UnlinksWithoutTouchingTheTrash()
    {
        WriteSession("session-1.mp4", "Overwatch", "The clutch");

        _host.DeleteContent(new DeleteContentParameters
        {
            FileName = "sessions/session-1.mp4",
            Permanent = true,
        });

        Assert.False(File.Exists(Path.Combine(_contentRoot, "sessions", "session-1.mp4")));
        Assert.False(File.Exists(MetadataPath("session-1.mp4")));
        Assert.Empty(_host.TrashEntries());
        Assert.False(Directory.Exists(Path.Combine(_contentRoot, ".trash")),
            "a permanent delete must not create a trash entry");
    }

    [Fact]
    public void DeleteMultipleContent_TrashesEveryItem()
    {
        WriteSession("session-1.mp4", "Overwatch", null);
        WriteSession("session-2.mp4", "Overwatch", null);

        _host.DeleteMultipleContent(new DeleteMultipleContentParameters
        {
            Items =
            [
                new DeleteContentParameters { FileName = "sessions/session-1.mp4" },
                new DeleteContentParameters { FileName = "sessions/session-2.mp4" },
            ],
        });

        Assert.Equal(2, _host.TrashEntries().Count);
        Assert.Empty(_host.ListContent());
    }

    // ---- restore ----

    [Fact]
    public void RestoreTrash_PutsTheVideoAndItsRecordsBack()
    {
        WriteSession("session-1.mp4", "Overwatch", "The clutch");
        WriteThumbnail("session-1.mp4");

        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        var entry = Assert.Single(_host.TrashEntries());

        _host.RestoreTrash(new RestoreTrashParameters { EntryIds = [entry.Id] });

        Assert.True(File.Exists(Path.Combine(_contentRoot, "sessions", "session-1.mp4")));
        Assert.True(File.Exists(MetadataPath("session-1.mp4")));
        Assert.True(File.Exists(Path.Combine(_contentRoot, "metadata", "thumbnails", "session-1.mp4.jpg")));
        Assert.Empty(_host.TrashEntries());
        Assert.False(Directory.Exists(EntryDirectory(entry.Id)), "a restored entry leaves nothing behind");

        // The bookmarks came back with the record, which is the whole point of moving it.
        var item = Assert.Single(_host.ListContent());
        Assert.Equal("The clutch", item.Title);
        Assert.Equal("Overwatch", item.Game);
        Assert.Single(item.Bookmarks!);
    }

    // A name that was taken again while the item sat in the bin must not be overwritten: the
    // restored file gets a free name, and every record keyed to the old one follows it.
    [Fact]
    public void RestoreTrash_NeverOverwritesAFileThatTookTheNameBack()
    {
        WriteSession("session-1.mp4", "Overwatch", "The clutch");
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        var entry = Assert.Single(_host.TrashEntries());

        // A new recording claimed the name while the old one was in the bin.
        File.WriteAllText(Path.Combine(_contentRoot, "sessions", "session-1.mp4"), "the newcomer");

        _host.RestoreTrash(new RestoreTrashParameters { EntryIds = [entry.Id] });

        Assert.Equal("the newcomer",
            File.ReadAllText(Path.Combine(_contentRoot, "sessions", "session-1.mp4")));
        var restored = Path.Combine(_contentRoot, "sessions", "session-1-restored-1.mp4");
        Assert.True(File.Exists(restored), "the restored file must land under a free name");
        Assert.Equal("session", File.ReadAllText(restored));

        // The records followed the new key, and the record's own link back to the video was re-pointed.
        Assert.True(File.Exists(MetadataPath("session-1-restored-1.mp4")));
        var record = JsonSerializer.Deserialize<RecordingMetadata>(
            File.ReadAllText(MetadataPath("session-1-restored-1.mp4")), SettingsSerialization.Options)!;
        Assert.Equal("sessions/session-1-restored-1.mp4", record.VideoPath);
        Assert.Equal("The clutch", record.Title);

        Assert.Empty(_host.TrashEntries());
    }

    [Fact]
    public void RestoreTrash_UnknownEntry_IsRefusedRatherThanThrown()
    {
        _host.RestoreTrash(new RestoreTrashParameters { EntryIds = ["../../etc", "no-such-entry"] });

        Assert.Empty(_host.TrashEntries());
    }

    // ---- purge ----

    [Fact]
    public void PurgeTrash_WithoutIds_EmptiesTheWholeBin()
    {
        WriteSession("session-1.mp4", null, null);
        WriteSession("session-2.mp4", null, null);
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-2.mp4" });
        Assert.Equal(2, _host.TrashEntries().Count);

        _host.PurgeTrash(new PurgeTrashParameters());

        Assert.Empty(_host.TrashEntries());
        Assert.Empty(Directory.GetDirectories(Path.Combine(_contentRoot, ".trash")));
    }

    [Fact]
    public void PurgeTrash_WithIds_RemovesOnlyThose()
    {
        WriteSession("session-1.mp4", null, null);
        WriteSession("session-2.mp4", null, null);
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-2.mp4" });

        var first = _host.TrashEntries().Single(entry => entry.FileName == "session-1.mp4");
        _host.PurgeTrash(new PurgeTrashParameters { EntryIds = [first.Id] });

        var remaining = Assert.Single(_host.TrashEntries());
        Assert.Equal("session-2.mp4", remaining.FileName);
    }

    // The retention window, in the two directions that matter: an entry past it goes, an entry
    // inside it stays.
    [Fact]
    public void PurgeExpiredTrash_HonoursTheRetentionWindow()
    {
        WriteSession("old.mp4", null, null);
        WriteSession("fresh.mp4", null, null);
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/old.mp4" });
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/fresh.mp4" });

        var old = _host.TrashEntries().Single(entry => entry.FileName == "old.mp4");
        BackdateEntry(old.Id, TimeSpan.FromHours(30));

        _host.PurgeExpiredTrash();

        var remaining = Assert.Single(_host.TrashEntries());
        Assert.Equal("fresh.mp4", remaining.FileName);
    }

    // Retention <= 0 disables the automatic purge outright: the bin then keeps what it holds until
    // it is emptied by hand, and purgeAt goes out as 0 so nothing counts down in the UI.
    [Fact]
    public void PurgeExpiredTrash_IsDisabled_WhenTheRetentionIsNotPositive()
    {
        _store.Load().Recording.TrashRetentionHours = 0;
        WriteSession("old.mp4", null, null);
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/old.mp4" });
        BackdateEntry(_host.TrashEntries().Single().Id, TimeSpan.FromDays(400));

        _host.PurgeExpiredTrash();

        var entry = Assert.Single(_host.TrashEntries());
        Assert.Equal(0, entry.PurgeAt);
    }

    // ---- the bin is not part of the library ----

    [Fact]
    public void ListContent_AndTheContentServer_BothIgnoreTheTrash()
    {
        var trashed = Path.Combine(_contentRoot, ".trash", "20260818-101112123-abcdef01", "files",
            "sessions", "session-1.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(trashed)!);
        File.WriteAllText(trashed, "session");

        Assert.Empty(_host.ListContent());

        // The same exclusion at the traversal guard, so nothing in the bin can be streamed, clipped
        // or deleted through a wire path either.
        Assert.Null(ContentServer.ResolveWithinRoot(_contentRoot,
            ".trash/20260818-101112123-abcdef01/files/sessions/session-1.mp4"));
        Assert.Null(ContentServer.ResolveWithinRoot(_contentRoot, ".trash"));
        Assert.NotNull(ContentServer.ResolveWithinRoot(_contentRoot, "sessions/session-1.mp4"));
    }

    // ---- a bin whose own records are unusable ----

    // The entry record is a convenience, not the truth: the mirror under files/ already says where
    // everything came from. A hand-edited or half-written record must therefore neither hide the
    // entry nor take the app down — and it is never rewritten over, the same rule the metadata
    // store follows.
    [Fact]
    public void ACorruptEntryRecord_StillListsAndStillRestores()
    {
        WriteSession("session-1.mp4", "Overwatch", "The clutch");
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        var entryId = Assert.Single(_host.TrashEntries()).Id;

        var recordPath = Path.Combine(EntryDirectory(entryId), "entry.json");
        File.WriteAllText(recordPath, "{ this is not json");

        var entry = Assert.Single(_host.TrashEntries());
        Assert.Equal(entryId, entry.Id);
        Assert.Equal("session-1.mp4", entry.FileName);
        Assert.Equal("recording", entry.ContentType);

        _host.RestoreTrash(new RestoreTrashParameters { EntryIds = [entryId] });

        Assert.True(File.Exists(Path.Combine(_contentRoot, "sessions", "session-1.mp4")));
        Assert.True(File.Exists(MetadataPath("session-1.mp4")), "the record moved back with its video");
        Assert.Empty(_host.TrashEntries());
    }

    // A crash between the move and the record write leaves an entry with no record at all. The
    // files are the ones that matter, so the entry still lists and still restores.
    [Fact]
    public void AnEntryWithNoRecordAtAll_StillListsAndStillRestores()
    {
        WriteSession("session-1.mp4", null, null);
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        var entryId = Assert.Single(_host.TrashEntries()).Id;
        File.Delete(Path.Combine(EntryDirectory(entryId), "entry.json"));

        var entry = Assert.Single(_host.TrashEntries());
        Assert.Equal("session-1.mp4", entry.FileName);

        _host.RestoreTrash(new RestoreTrashParameters { EntryIds = [entry.Id] });

        Assert.True(File.Exists(Path.Combine(_contentRoot, "sessions", "session-1.mp4")));
        Assert.Empty(_host.TrashEntries());
    }

    // ---- the bin follows the recording root ----

    [Fact]
    public void ChangingTheOutputDirectory_MovesTheBinWithIt()
    {
        var moved = Path.Combine(_contentRoot, "moved-recordings");
        _host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            recording = new { outputDirectory = moved },
        }));

        Directory.CreateDirectory(Path.Combine(moved, "sessions"));
        File.WriteAllText(Path.Combine(moved, "sessions", "session-1.mp4"), "session");

        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });

        Assert.Single(_host.TrashEntries());
        Assert.True(Directory.Exists(Path.Combine(moved, ".trash")),
            "the trash must sit under the new recording root");
        Assert.False(Directory.Exists(Path.Combine(_contentRoot, ".trash")));
    }

    // ---- helpers ----

    // A restore that cannot put every file back used to delete the entry anyway, destroying exactly
    // the files it had just logged as keeping. The record carries the recording's title and bookmarks;
    // a duration probe re-creating a record under the same key is enough to trigger it, because the
    // metadata store keys by bare file name.
    [Fact]
    public void Restore_WhenARecordCannotBePutBack_KeepsTheEntryInsteadOfDestroyingIt()
    {
        WriteSession("session-1.mp4", "Overwatch", "The clutch");
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        var entry = Assert.Single(_host.TrashEntries());

        // Something re-creates a record under the same key while the video sits in the trash.
        Directory.CreateDirectory(Path.Combine(_contentRoot, "metadata"));
        File.WriteAllText(MetadataPath("session-1.mp4"), "{}");

        _host.RestoreTrash(new RestoreTrashParameters { EntryIds = [entry.Id] });

        // The video came back...
        Assert.True(File.Exists(Path.Combine(_contentRoot, "sessions", "session-1.mp4")));

        // ...but the record that could not be put back is still in the trash, not deleted.
        Assert.True(Directory.Exists(EntryDirectory(entry.Id)),
            "the entry must survive when it still holds files that could not be restored");
        Assert.True(
            File.Exists(Path.Combine(EntryDirectory(entry.Id), "files", "metadata", "session-1.mp4.metadata.json")),
            "the trapped record must still be on disk, not destroyed with the entry");
        Assert.Single(_host.TrashEntries());
    }

    // Null here means "the frame carried parameters this host could not parse", never "no parameters":
    // the dispatch substitutes an explicit object for the parameterless whole-bin case. Treating the
    // two alike let a malformed frame empty the entire trash.
    [Fact]
    public void PurgeTrash_WithUnparseableParameters_LeavesTheBinAlone()
    {
        WriteSession("session-1.mp4", "Overwatch", "The clutch");
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        Assert.Single(_host.TrashEntries());

        _host.PurgeTrash(null);

        Assert.Single(_host.TrashEntries());
    }

    // The same thing over the dispatch table, which is where the absent/unparseable distinction is
    // actually made.
    [Theory]
    [InlineData("{\"entryIds\":\"not-a-list\"}")]
    [InlineData("\"not-an-object\"")]
    [InlineData("123")]
    public void PurgeTrash_OverTheDispatch_WithAMalformedFrame_LeavesTheBinAlone(string parametersJson)
    {
        WriteSession("session-1.mp4", "Overwatch", "The clutch");
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        Assert.Single(_host.TrashEntries());

        var controller = new AppController(_host);
        var parameters = JsonSerializer.Deserialize<JsonElement>(parametersJson);
        controller.Handle("PurgeTrash", parameters, new ClientHandle((_, _) => { }));

        Assert.Single(_host.TrashEntries());
    }

    // And the parameterless frame still means the whole bin.
    [Fact]
    public void PurgeTrash_OverTheDispatch_WithNoParameters_EmptiesTheBin()
    {
        WriteSession("session-1.mp4", "Overwatch", "The clutch");
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        Assert.Single(_host.TrashEntries());

        var controller = new AppController(_host);
        controller.Handle("PurgeTrash", null, new ClientHandle((_, _) => { }));

        Assert.Empty(_host.TrashEntries());
    }

    private void WriteSession(string fileName, string? game, string? title)
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, fileName), "session");

        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = $"sessions/{fileName}",
            Game = game ?? string.Empty,
            Title = title,
            Bookmarks = { new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(3) } },
        });
    }

    private string WriteThumbnail(string videoFileName)
    {
        var thumbnails = Path.Combine(_contentRoot, "metadata", "thumbnails");
        Directory.CreateDirectory(thumbnails);
        var path = Path.Combine(thumbnails, $"{videoFileName}.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF]);
        return path;
    }

    private string MetadataPath(string videoFileName) =>
        Path.Combine(_contentRoot, "metadata", $"{videoFileName}.metadata.json");

    private string EntryDirectory(string entryId) => Path.Combine(_contentRoot, ".trash", entryId);

    // Rewrites an entry's deletedAt so a retention window that is hours long can be crossed in a
    // test without waiting for it.
    private void BackdateEntry(string entryId, TimeSpan age)
    {
        var path = Path.Combine(EntryDirectory(entryId), "entry.json");
        var node = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        var rewritten = new Dictionary<string, JsonElement>();
        foreach (var property in node.EnumerateObject())
            rewritten[property.Name] = property.Value.Clone();
        rewritten["deletedAt"] = JsonSerializer.SerializeToElement(
            DateTimeOffset.UtcNow.Subtract(age).ToUnixTimeSeconds());
        File.WriteAllText(path, JsonSerializer.Serialize(rewritten));
    }
}
