// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using Tript.Media;

namespace Tript.App.Content;

// The on-disk thumbnail cache the library grid is drawn from. Cache hits are served directly, while
// misses are placed on a bounded background queue. An HTTP request never waits for ffmpeg: keeping a
// thumbnail response open can occupy every browser connection to the same origin and prevent the
// player's range request from reaching the server at all.
internal sealed class ThumbnailStore : IDisposable
{
    private const string CacheVersion = "2";
    private const int MaxPendingExtractions = 32;
    private static readonly TimeSpan WorkerShutdownWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailedExtractionCooldown = TimeSpan.FromMinutes(5);

    private readonly Lazy<IThumbnailExtractor?> _extractor;
    private readonly BlockingCollection<GenerationJob> _jobs =
        new(new ConcurrentQueue<GenerationJob>(), MaxPendingExtractions);
    private readonly Dictionary<string, GenerationJob> _pending;
    private readonly Dictionary<string, FailedGeneration> _failed;
    private readonly Dictionary<string, long> _fileVersions;
    private readonly object _stateGate = new();
    private readonly Thread _worker;

    private string _thumbnailRoot;
    private long _rootVersion;
    private bool _disposed;

    internal ThumbnailStore(string thumbnailRoot, Func<IThumbnailExtractor?> extractorFactory)
    {
        _thumbnailRoot = thumbnailRoot;
        _extractor = new Lazy<IThumbnailExtractor?>(extractorFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _pending = new Dictionary<string, GenerationJob>(comparer);
        _failed = new Dictionary<string, FailedGeneration>(comparer);
        _fileVersions = new Dictionary<string, long>(comparer);
        _worker = new Thread(ProcessJobs)
        {
            IsBackground = true,
            Name = "Tript.App.Thumbnail.Generation",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    internal void UpdateRoot(string thumbnailRoot)
    {
        lock (_stateGate)
        {
            _thumbnailRoot = thumbnailRoot;
            _rootVersion++;
            _failed.Clear();
        }
    }

    internal string PathFor(string videoFileName)
    {
        lock (_stateGate)
            return Path.Combine(_thumbnailRoot, $"{videoFileName}.jpg");
    }

    // Returns a usable cache entry immediately. A miss is coalesced into the bounded background
    // queue and returns null, whether it was accepted, already pending, or shed because the queue is
    // full. All three outcomes deliberately release the browser connection at once.
    internal string? GetOrQueue(string videoPath)
        => GetOrQueue(videoPath, out _);

    private string? GetOrQueue(string videoPath, out Task<string?> completion)
    {
        completion = Task.FromResult<string?>(null);
        var fileName = Path.GetFileName(videoPath);
        if (fileName.Length == 0)
            return null;

        GenerationJob job;
        lock (_stateGate)
        {
            if (_disposed)
                return null;

            var cached = Path.Combine(_thumbnailRoot, $"{fileName}.jpg");
            if (IsFresh(cached, videoPath))
            {
                completion = Task.FromResult<string?>(cached);
                return cached;
            }

            var key = PendingKey(_rootVersion, videoPath);
            if (_pending.TryGetValue(key, out var pending))
            {
                completion = pending.Completion.Task;
                return null;
            }

            if (!TryReadSourceStamp(videoPath, out var source))
                return null;

            if (_failed.TryGetValue(key, out var failed))
            {
                if (failed.Source == source && failed.RetryAfterUtc > DateTime.UtcNow)
                    return null;
                _failed.Remove(key);
            }

            var fileVersion = _fileVersions.GetValueOrDefault(fileName);
            job = new GenerationJob(key, videoPath, fileName, cached, source, _rootVersion, fileVersion);
            _pending.Add(key, job);
            completion = job.Completion.Task;
            if (!_jobs.TryAdd(job))
            {
                _pending.Remove(key);
                job.Completion.TrySetResult(null);
                return null;
            }
        }

        return null;
    }

    // Test/support seam for callers that need the generated result rather than HTTP's immediate-miss
    // contract. Production thumbnail requests use GetOrQueue and never await this completion.
    internal Task<string?> EnsureAsync(string videoPath)
    {
        var cached = GetOrQueue(videoPath, out var completion);
        if (cached is not null)
            return Task.FromResult<string?>(cached);
        return completion;
    }

    private void ProcessJobs()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            string? result = null;
            var attempted = false;
            try
            {
                if (CanRun(job))
                {
                    var extractor = _extractor.Value;
                    if (extractor is not null)
                    {
                        attempted = true;
                        result = Generate(extractor, job);
                    }
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"Tript.App: could not build a thumbnail for '{job.VideoPath}': {exception.Message}");
            }
            finally
            {
                lock (_stateGate)
                {
                    if (result is not null)
                    {
                        _failed.Remove(job.Key);
                    }
                    else if (attempted && IsCurrent(job) && SourceMatches(job))
                    {
                        // A corrupt file or unsupported codec must not launch ffmpeg once per card
                        // retry. A changed source bypasses the cooldown because its stamp differs.
                        _failed[job.Key] = new FailedGeneration(
                            job.Source, DateTime.UtcNow + FailedExtractionCooldown);
                    }
                    if (_pending.TryGetValue(job.Key, out var pending) && ReferenceEquals(pending, job))
                        _pending.Remove(job.Key);
                }
                job.Completion.TrySetResult(result);
            }
        }
    }

    private bool CanRun(GenerationJob job)
    {
        lock (_stateGate)
            return IsCurrent(job) && SourceMatches(job);
    }

    private string? Generate(IThumbnailExtractor extractor, GenerationJob job)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(job.CachedPath)!);
        var temporary = $"{job.CachedPath}.{Guid.NewGuid():N}.part";
        try
        {
            if (!extractor.TryExtract(job.VideoPath, temporary))
                return null;

            // Delete/root changes and source replacement can happen while ffmpeg is running. The
            // short state lock closes the check-to-publish race; extraction itself never holds it.
            lock (_stateGate)
            {
                if (!IsCurrent(job) || !SourceMatches(job))
                    return null;

                File.Move(temporary, job.CachedPath, overwrite: true);
                File.WriteAllText(VersionPath(job.CachedPath), CacheVersion);
                return job.CachedPath;
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
    }

    private bool IsCurrent(GenerationJob job) =>
        !_disposed
        && job.RootVersion == _rootVersion
        && job.FileVersion == _fileVersions.GetValueOrDefault(job.FileName);

    private static bool SourceMatches(GenerationJob job) =>
        TryReadSourceStamp(job.VideoPath, out var current) && current == job.Source;

    internal bool Delete(string videoFileName)
    {
        lock (_stateGate)
        {
            InvalidateLocked(videoFileName);
            try
            {
                var path = Path.Combine(_thumbnailRoot, $"{videoFileName}.jpg");
                File.Delete(path);
                File.Delete(VersionPath(path));
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Tript.App: could not delete a cached thumbnail: {exception.Message}");
                return false;
            }
        }
    }

    // Prevents an extraction already in progress from publishing after the caller moves the source
    // and its current cache entry elsewhere. A later request may enqueue a fresh generation if the
    // source remains in place (for example, when a trash transaction fails).
    internal void Invalidate(string videoFileName)
    {
        lock (_stateGate)
            InvalidateLocked(videoFileName);
    }

    private void InvalidateLocked(string videoFileName) =>
        _fileVersions[videoFileName] = _fileVersions.GetValueOrDefault(videoFileName) + 1;

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

    private static bool TryReadSourceStamp(string path, out SourceStamp stamp)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                stamp = default;
                return false;
            }

            stamp = new SourceStamp(file.Length, file.LastWriteTimeUtc);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stamp = default;
            return false;
        }
    }

    private static string PendingKey(long rootVersion, string videoPath) => $"{rootVersion}:{videoPath}";

    private static string VersionPath(string cached) => $"{cached}.version";

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _jobs.CompleteAdding();
        }

        // The extractor has its own hard process timeout. Do not make app shutdown wait on it: this
        // is a background cache and the worker is marked background specifically for that guarantee.
        if (_worker.Join(WorkerShutdownWait))
            _jobs.Dispose();
    }

    private readonly record struct SourceStamp(long Length, DateTime LastWriteTimeUtc);
    private readonly record struct FailedGeneration(SourceStamp Source, DateTime RetryAfterUtc);

    private sealed record GenerationJob(
        string Key,
        string VideoPath,
        string FileName,
        string CachedPath,
        SourceStamp Source,
        long RootVersion,
        long FileVersion)
    {
        internal TaskCompletionSource<string?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
