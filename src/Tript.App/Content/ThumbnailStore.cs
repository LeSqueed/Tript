// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;

namespace Tript.App.Content;

// The on-disk thumbnail cache the library's grid is drawn from. One JPEG per video, generated on
// first request and served from disk afterwards.
//
// Why a cache at all: the library is a paginated grid of thumbnail cards, so a single render asks
// for every visible card's thumbnail at once. Generating on each request would spawn one ffmpeg per
// card per render — two dozen decoders for a scroll — which is why the file on disk, not the frame,
// is the unit of work here.
//
// Where it lives: <recordingRoot>/metadata/thumbnails/<videoFileName>.jpg. The metadata tree is
// already where per-video derived records live (RecordingMetadataStore, ClipTitleStore), keyed the
// same way — by the video's own file name, so a record is addressable without parsing anything — and
// it already moves with the recording root through the same UpdateRoot call. Two consequences that
// decided it over a thumbnails/ sibling:
//
//   * the recordings and clips directories stay plain MP4s, which is a documented invariant of the
//     storage layout, and ListContent enumerates *.mp4 under the root recursively, so nothing new
//     appears in the library;
//   * the cascade delete is already there. Deleting content removes its metadata records; the
//     thumbnail is one more keyed record removed in the same place (AppHost.DeleteContent), so a
//     deleted video cannot leave an orphaned image behind.
//
// The subdirectory keeps a pile of binaries out of the hand-readable record directory and makes
// "drop the whole cache" a single directory delete.
internal sealed class ThumbnailStore
{
    private const string CacheVersion = "2";
    // A first render of a full page of cards arrives as a burst of concurrent requests, each a
    // cache miss. Unbounded, that is one ffmpeg per card at once, which on a recording machine
    // competes with the encoder for the same cores.
    private const int MaxConcurrentExtractions = 3;

    // How long a request waits for its turn before giving up. Longer than the extraction timeout
    // times the queue depth would mean a browser request waiting on a queue instead of being told
    // "no thumbnail yet"; the frontend can re-request on the next render, so giving up is cheap.
    private static readonly TimeSpan QueueWait = TimeSpan.FromSeconds(20);

    private readonly Lazy<IThumbnailExtractor?> _extractor;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentExtractions, MaxConcurrentExtractions);

    // One gate per video file name, so two concurrent requests for the same card do not both
    // run ffmpeg (and do not both write the same file). Requests for different cards never contend.
    private readonly Dictionary<string, SemaphoreSlim> _perFileGates = new(StringComparer.Ordinal);

    // Guards _thumbnailRoot alone. Always taken innermost (a per-file gate may be held while it is
    // acquired; never the other way round).
    private readonly object _rootGate = new();

    private string _thumbnailRoot;

    // The extractor is resolved lazily and at most once: building it locates ffmpeg on PATH, which
    // runs `ffmpeg -version` to verify the binary, and doing that per request would be two extra
    // processes per card. A machine with no ffmpeg resolves to null once and every request then
    // answers "no thumbnail" without touching the file system.
    internal ThumbnailStore(string thumbnailRoot, Func<IThumbnailExtractor?> extractorFactory)
    {
        _thumbnailRoot = thumbnailRoot;
        _extractor = new Lazy<IThumbnailExtractor?>(extractorFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // Switches the cache to a new tree (a settings change that moves the recording output directory
    // moves the metadata tree, and the cache inside it, with it).
    internal void UpdateRoot(string thumbnailRoot)
    {
        lock (_rootGate)
        {
            _thumbnailRoot = thumbnailRoot;
        }
    }

    internal string PathFor(string videoFileName) => Path.Combine(Root, $"{videoFileName}.jpg");

    private string Root
    {
        get
        {
            lock (_rootGate)
                return _thumbnailRoot;
        }
    }

    // The cached thumbnail for a video, generating it if there is not a usable one yet. Returns
    // null when no image can be produced — a missing or corrupt source, no ffmpeg on the machine,
    // an extraction that failed or overran, an unwritable cache directory.
    internal async Task<string?> EnsureAsync(string videoPath)
    {
        var fileName = Path.GetFileName(videoPath);
        if (fileName.Length == 0)
            return null;

        var cached = PathFor(fileName);
        if (IsFresh(cached, videoPath))
            return cached;

        var gate = GateFor(fileName);
        await gate.WaitAsync();
        try
        {
            // Re-checked under the gate: while this request waited, the request it was queued behind
            // may have generated exactly this file.
            if (IsFresh(cached, videoPath))
                return cached;

            var extractor = _extractor.Value;
            if (extractor is null)
                return null;

            if (!await _concurrency.WaitAsync(QueueWait))
                return null;

            try
            {
                return Generate(extractor, videoPath, cached);
            }
            catch (Exception exception)
            {
                // This runs on an HttpListener worker. An exception escaping here would become a 500
                // for a card, or worse an unhandled exception on the listener's thread pool, so
                // every failure is reported as "no thumbnail" instead.
                Console.Error.WriteLine($"Tript.App: could not build a thumbnail for '{videoPath}': {exception.Message}");
                return null;
            }
            finally
            {
                _concurrency.Release();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string? Generate(IThumbnailExtractor extractor, string videoPath, string cached)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);

        // Written to a temporary name and moved into place: a request that arrives while ffmpeg is
        // still writing must not be served a half-written JPEG, and File.Move within one directory
        // is atomic enough for that (the reader either sees the old file or the new one).
        var temporary = $"{cached}.{Environment.ProcessId:x}-{Environment.CurrentManagedThreadId:x}.part";
        try
        {
            if (!extractor.TryExtract(videoPath, temporary))
                return null;

            File.Move(temporary, cached, overwrite: true);
            File.WriteAllText(VersionPath(cached), CacheVersion);
            return cached;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (IOException) { /* swept on the next attempt */ }
            }
        }
    }

    // Removes a video's cached thumbnail, when there is one. Part of the cascade-delete contract: a
    // deleted video takes its metadata records with it, and the thumbnail is one of them, so the
    // cache never keeps an image for a video that is gone (and a new recording that happened to
    // reuse the name could never inherit the old image).
    internal bool Delete(string videoFileName)
    {
        try
        {
            File.Delete(PathFor(videoFileName));
            File.Delete(VersionPath(PathFor(videoFileName)));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not delete a cached thumbnail: {exception.Message}");
            return false;
        }
    }

    // Whether the cached image can be served as-is. Non-empty, and no older than the video: a video
    // replaced in place under the same name (a re-record, a restored backup) must not keep serving
    // the previous file's frame.
    private static bool IsFresh(string cached, string videoPath)
    {
        try
        {
            var image = new FileInfo(cached);
            if (!image.Exists || image.Length == 0)
                return false;

            if (!File.Exists(VersionPath(cached))
                || !string.Equals(File.ReadAllText(VersionPath(cached)), CacheVersion, StringComparison.Ordinal))
                return false;

            var video = new FileInfo(videoPath);
            return !video.Exists || image.LastWriteTimeUtc >= video.LastWriteTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string VersionPath(string cached) => $"{cached}.version";

    private SemaphoreSlim GateFor(string fileName)
    {
        lock (_perFileGates)
        {
            if (!_perFileGates.TryGetValue(fileName, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _perFileGates[fileName] = gate;
            }

            return gate;
        }
    }
}
