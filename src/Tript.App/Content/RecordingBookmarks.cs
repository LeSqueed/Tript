// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Core;
using Tript.Settings;

namespace Tript.App.Content;

internal sealed class RecordingBookmarks
{
    private readonly RecordingMetadataStore _metadata;
    private readonly Func<string, string> _wirePath;
    private readonly Action<string> _reportError;

    internal RecordingBookmarks(RecordingMetadataStore metadata, Func<string, string> wirePath,
        Action<string> reportError)
    {
        _metadata = metadata;
        _wirePath = wirePath;
        _reportError = reportError;
    }

    internal static Bookmark Create(AddBookmarkParameters parameters) => new()
    {
        Id = Guid.TryParse(parameters.Id, out var id) ? id : Guid.NewGuid(),
        Type = Enum.TryParse<BookmarkType>(parameters.Type, ignoreCase: true, out var type) ? type : BookmarkType.Manual,
        Time = TimeSpan.FromSeconds(parameters.Time),
    };

    internal bool TryAdd(string target, Bookmark bookmark)
    {
        var fileName = Path.GetFileName(target);
        lock (_metadata.WriteGate)
        {
            var existing = _metadata.Read(fileName);
            if (existing.MustNotBeOverwritten)
            {
                Log.Warning("{FileName} has a metadata record that could not be read ({Failure}); the bookmark is refused rather than replacing it.", fileName, existing.Failure);
                _reportError(
                    "The bookmark could not be saved: this recording's metadata record could not be read, and overwriting it would lose its game and existing bookmarks.");
                return false;
            }

            var metadata = existing.Record ?? new RecordingMetadata { VideoPath = _wirePath(target) };
            metadata.Bookmarks.Add(bookmark);
            if (_metadata.Save(metadata))
                return true;
        }

        _reportError("The bookmark could not be saved, check the recording folder is writable.");
        return false;
    }

    internal bool TryRemove(string fileName, Guid id)
    {
        lock (_metadata.WriteGate)
        {
            var existing = _metadata.Read(fileName);
            if (existing.MustNotBeOverwritten)
            {
                Log.Warning("{FileName} has a metadata record that could not be read ({Failure}); the bookmark removal is refused rather than replacing it.", fileName, existing.Failure);
                _reportError("The bookmark could not be removed: this recording's metadata record could not be read.");
                return false;
            }

            var metadata = existing.Record;
            if (metadata is null || metadata.Bookmarks.RemoveAll(bookmark => bookmark.Id == id) == 0)
                return false;
            if (_metadata.Save(metadata))
                return true;
        }

        _reportError("The bookmark could not be removed, check the recording folder is writable.");
        return false;
    }
}
