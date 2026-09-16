// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using Tript.Media;
using Tript.Core;

namespace Tript.App.Content;

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
    private readonly HashSet<string> _verifiedVersions;
    private readonly Dictionary<string, int> _removing;
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
        var comparer = FilePaths.Comparer;
        _pending = new Dictionary<string, GenerationJob>(comparer);
        _failed = new Dictionary<string, FailedGeneration>(comparer);
        _fileVersions = new Dictionary<string, long>(comparer);
        _verifiedVersions = new HashSet<string>(comparer);
        _removing = new Dictionary<string, int>(comparer);
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
            _verifiedVersions.Clear();
        }
    }

    internal string PathFor(string videoFileName)
    {
        lock (_stateGate)
            return Path.Combine(_thumbnailRoot, $"{videoFileName}.jpg");
    }

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
            if (_disposed || _removing.ContainsKey(fileName))
                return null;

            var cached = Path.Combine(_thumbnailRoot, $"{fileName}.jpg");
            if (IsFresh(fileName, cached, videoPath))
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

            lock (_stateGate)
            {
                if (!IsCurrent(job) || !SourceMatches(job))
                    return null;

                File.Move(temporary, job.CachedPath, overwrite: true);
                File.WriteAllText(VersionPath(job.CachedPath), CacheVersion);
                _verifiedVersions.Add(job.FileName);
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

    internal static readonly TimeSpan ExtractionReleaseWait = TimeSpan.FromSeconds(10);

    internal RemovalHold HoldForRemoval(string videoFileName, TimeSpan wait)
    {
        Task[] running;
        lock (_stateGate)
        {
            _removing[videoFileName] = _removing.GetValueOrDefault(videoFileName) + 1;
            InvalidateLocked(videoFileName);
            running = _pending.Values
                .Where(job => FilePaths.Comparer.Equals(job.FileName, videoFileName))
                .Select(job => (Task)job.Completion.Task)
                .ToArray();
        }

        if (running.Length > 0)
            Task.WaitAll(running, wait);
        return new RemovalHold(this, videoFileName);
    }

    private void ReleaseRemoval(string videoFileName)
    {
        lock (_stateGate)
        {
            var holds = _removing.GetValueOrDefault(videoFileName) - 1;
            if (holds > 0)
                _removing[videoFileName] = holds;
            else
                _removing.Remove(videoFileName);
        }
    }

    internal readonly struct RemovalHold(ThumbnailStore store, string videoFileName) : IDisposable
    {
        public void Dispose() => store?.ReleaseRemoval(videoFileName);
    }

    internal void Invalidate(string videoFileName)
    {
        lock (_stateGate)
            InvalidateLocked(videoFileName);
    }

    private void InvalidateLocked(string videoFileName)
    {
        _fileVersions[videoFileName] = _fileVersions.GetValueOrDefault(videoFileName) + 1;
        _verifiedVersions.Remove(videoFileName);
    }

    private bool IsFresh(string fileName, string cached, string videoPath)
    {
        try
        {
            var image = new FileInfo(cached);
            if (!image.Exists || image.Length == 0)
                return false;

            if (!HasCurrentVersion(fileName, cached))
                return false;

            var video = new FileInfo(videoPath);
            return !video.Exists || image.LastWriteTimeUtc >= video.LastWriteTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool HasCurrentVersion(string fileName, string cached)
    {
        if (_verifiedVersions.Contains(fileName))
            return true;

        var versionPath = VersionPath(cached);
        if (!File.Exists(versionPath)
            || !string.Equals(File.ReadAllText(versionPath), CacheVersion, StringComparison.Ordinal))
            return false;

        _verifiedVersions.Add(fileName);
        return true;
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
