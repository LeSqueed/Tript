// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.App.Content;
using Tript.Core;
using Tript.Media;
using Tript.Recorder;
using Tript.Settings;

namespace Tript.App;

internal sealed partial class AppHost
{
    private sealed class LiveHighlightRegion
    {
        internal required TimeSpan Start { get; set; }

        internal required TimeSpan End { get; set; }

        internal HashSet<Guid> BookmarkIds { get; } = [];

        internal bool SaveRequested { get; set; }

        internal bool Abandoned { get; set; }
    }

    private static readonly TimeSpan LiveHighlightBoundaryGrace = TimeSpan.FromSeconds(1);

    private void RememberAutomaticClipBookmark(Bookmark bookmark)
    {
        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(_settingsStore.Load(), _currentGameId);
        bookmark.IsAutomaticClipCandidate = true;
        LiveHighlightRegion? regionToSchedule = null;
        CancellationToken token = default;
        lock (_automaticClipGate)
        {
            _automaticClipBookmarks.Add(bookmark);

            if (!_liveHighlightsEnabled || _liveHighlightCancellation is null)
                return;

            var start = bookmark.Time > before
                ? bookmark.Time - before
                : TimeSpan.Zero;
            var existing = _liveHighlightRegions.FirstOrDefault(region =>
                !region.SaveRequested && bookmark.Time - before <= region.End);
            if (existing is not null)
            {
                existing.End = existing.End > bookmark.Time + after
                    ? existing.End
                    : bookmark.Time + after;
                existing.BookmarkIds.Add(bookmark.Id);
                return;
            }

            regionToSchedule = new LiveHighlightRegion
            {
                Start = start,
                End = bookmark.Time + after,
            };
            regionToSchedule.BookmarkIds.Add(bookmark.Id);
            _liveHighlightRegions.Add(regionToSchedule);
            token = _liveHighlightCancellation.Token;
        }

        var task = SaveLiveAutomaticHighlightWhenReady(regionToSchedule, token);
        lock (_automaticClipGate)
            _liveHighlightTasks.Add(task);
    }

    private async Task SaveLiveAutomaticHighlightWhenReady(LiveHighlightRegion region,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var (before, _) = SettingsResolver.ResolveAutomaticClipWindow(_settingsStore.Load(), _currentGameId);
                TimeSpan delay;
                lock (_automaticClipGate)
                {
                    if (region.SaveRequested || !_liveHighlightsEnabled)
                        return;
                    delay = _recordingStartUtc + region.End + before + LiveHighlightBoundaryGrace - DateTime.UtcNow;
                }

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                lock (_automaticClipGate)
                {
                    if (region.SaveRequested || !_liveHighlightsEnabled)
                        return;
                    if (_recordingStartUtc + region.End + before + LiveHighlightBoundaryGrace > DateTime.UtcNow)
                        continue;
                    region.SaveRequested = true;
                }

                var recorder = _recorder;
                var sourcePath = _activeSessionPath;
                if (recorder is null || sourcePath is null)
                    return;

                var sourceSessionPath = RelativeToRoot(sourcePath);
                var replayDirectory = Path.Combine(Path.GetTempPath(), "Tript", "replay");
                Directory.CreateDirectory(replayDirectory);
                var saveElapsed = (DateTime.UtcNow - _recordingStartUtc).TotalSeconds;
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var accepted = recorder.SaveReplayBuffer(replayDirectory,
                    "tript-replay-%CCYY-%MM-%DD-%hh-%mm-%ss",
                    replayPath =>
                    {
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                CreateLiveAutomaticHighlight(region, sourceSessionPath, sourcePath,
                                    replayPath, saveElapsed);
                            }
                            catch (Exception exception)
                            {
                                Log.Error(exception, "AppHost: live automatic highlight failed for {SourcePath}",
                                    sourcePath);
                            }
                            finally
                            {
                                completed.TrySetResult();
                            }
                        });
                    });

                if (!accepted)
                {
                    lock (_automaticClipGate)
                        region.SaveRequested = false;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            lock (_automaticClipGate)
            {
                region.Abandoned = true;
                region.SaveRequested = false;
            }
            Log.Warning("AppHost: replay buffer save did not complete for live automatic highlight");
        }
        catch (Exception exception)
        {
            Log.Error(exception, "AppHost: live automatic highlight scheduling failed");
        }
    }

    private void CreateLiveAutomaticHighlight(LiveHighlightRegion region, string sourceSessionPath,
        string sourcePath, string replayPath, double replayEndSeconds, bool deleteReplay = true)
    {
        try
        {
            if (!File.Exists(replayPath) || IsAbandonedLiveRegion(region))
                return;

            var bufferSeconds = Math.Max(1,
                _settingsStore.Load().Buffer.Duration.TotalSeconds);
            var replayStartSeconds = Math.Max(0, replayEndSeconds - bufferSeconds);
            var localStart = Math.Max(0, region.Start.TotalSeconds - replayStartSeconds);
            var localEnd = region.End.TotalSeconds - replayStartSeconds;
            if (localEnd <= localStart)
                return;

            _clipEngine ??= BuildClipEngine();
            var outputDirectory = HighlightsDirectoryForSource(sourcePath);
            var outputPath = Path.Combine(outputDirectory,
                $"{Path.GetFileNameWithoutExtension(sourcePath)}-highlight-live-{Guid.NewGuid():N}.mp4");
            var localRegion = new ClipRegion(TimeSpan.FromSeconds(localStart),
                TimeSpan.FromSeconds(localEnd));
            var results = _clipEngine.CreateClips(new ClipRequest
            {
                OperationId = $"automatic-live-{Guid.NewGuid():N}",
                SourcePath = replayPath,
                SourceSessionPath = sourceSessionPath,
                Regions = [localRegion],
                Mode = ClipMode.Combine,
                OutputPath = outputPath,
                EncoderFamily = "libx264",
                PreferStreamCopy = true,
            });

            if (IsAbandonedLiveRegion(region))
            {
                foreach (var result in results)
                {
                    try { File.Delete(result); }
                    catch (IOException exception) { Log.Warning(exception, "AppHost: abandoned live highlight could not be removed"); }
                    catch (UnauthorizedAccessException exception) { Log.Warning(exception, "AppHost: abandoned live highlight could not be removed"); }
                }
                return;
            }

            var metadataSaved = true;
            var highlightsOnlySession = _activeRecordingMode is RecordingMode.ReplayBufferOnly;
            foreach (var result in results)
            {
                metadataSaved &= _clipTitles.SaveAutomatic(Path.GetFileName(result), sourceSessionPath,
                    replayStartSeconds + localStart, replayStartSeconds + localEnd, highlightsOnlySession);
            }
            AttachGameToClips(results, sourceSessionPath);
            if (!metadataSaved)
            {
                foreach (var result in results)
                {
                    try { File.Delete(result); }
                    catch (IOException exception) { Log.Warning(exception, "AppHost: live highlight could not be removed after metadata failure"); }
                    catch (UnauthorizedAccessException exception) { Log.Warning(exception, "AppHost: live highlight could not be removed after metadata failure"); }
                }
                return;
            }

            lock (_automaticClipGate)
            {
                foreach (var bookmarkId in region.BookmarkIds)
                    _liveHighlightBookmarkIds.Add(bookmarkId);
            }
            PushContent();
        }
        finally
        {
            if (deleteReplay)
            {
                try { File.Delete(replayPath); }
                catch (IOException exception) { Log.Warning(exception, "AppHost: replay temporary file could not be removed"); }
                catch (UnauthorizedAccessException exception) { Log.Warning(exception, "AppHost: replay temporary file could not be removed"); }
            }
        }
    }

    private bool IsAbandonedLiveRegion(LiveHighlightRegion region)
    {
        lock (_automaticClipGate)
            return region.Abandoned;
    }

    private void StopLiveAutomaticHighlights()
    {
        Task[] tasks;
        CancellationTokenSource? cancellation;
        lock (_automaticClipGate)
        {
            _liveHighlightsEnabled = false;
            cancellation = _liveHighlightCancellation;
            cancellation?.Cancel();
            _liveHighlightCancellation = null;
            tasks = _liveHighlightTasks.ToArray();
        }

        try
        {
            Task.WaitAll(tasks, TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is AggregateException or ObjectDisposedException)
        {
            Log.Debug(exception, "AppHost: live automatic highlight tasks did not all settle before stop");
        }
        finally
        {
            lock (_automaticClipGate)
            {
                foreach (var region in _liveHighlightRegions)
                {
                    if (region.SaveRequested && !_liveHighlightBookmarkIds.Overlaps(region.BookmarkIds))
                    {
                        region.Abandoned = true;
                        region.SaveRequested = false;
                    }
                }
            }
            cancellation?.Dispose();
        }

        SavePendingLiveHighlightsAtStop();
    }

    private void SavePendingLiveHighlightsAtStop()
    {
        var recorder = _recorder;
        var sourcePath = _activeSessionPath;
        if (recorder is null || sourcePath is null)
            return;

        List<LiveHighlightRegion> pending;
        lock (_automaticClipGate)
        {
            pending = _liveHighlightRegions
                .Where(region => !region.Abandoned
                    && !region.SaveRequested
                    && !_liveHighlightBookmarkIds.Overlaps(region.BookmarkIds))
                .ToList();
            foreach (var region in pending)
                region.SaveRequested = true;
        }

        if (pending.Count == 0)
            return;

        var sourceSessionPath = RelativeToRoot(sourcePath);
        var replayDirectory = Path.Combine(Path.GetTempPath(), "Tript", "replay");
        Directory.CreateDirectory(replayDirectory);
        var saveElapsed = (DateTime.UtcNow - _recordingStartUtc).TotalSeconds;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = recorder.SaveReplayBuffer(replayDirectory,
            "tript-replay-%CCYY-%MM-%DD-%hh-%mm-%ss",
            replayPath =>
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        foreach (var region in pending)
                            CreateLiveAutomaticHighlight(region, sourceSessionPath, sourcePath,
                                replayPath, saveElapsed, deleteReplay: false);
                    }
                    catch (Exception exception)
                    {
                        Log.Error(exception, "AppHost: pending live automatic highlights failed at stop");
                    }
                    finally
                    {
                        try { File.Delete(replayPath); }
                        catch (IOException exception) { Log.Warning(exception, "AppHost: stop replay temporary file could not be removed"); }
                        catch (UnauthorizedAccessException exception) { Log.Warning(exception, "AppHost: stop replay temporary file could not be removed"); }
                        completed.TrySetResult();
                    }
                });
            });

        if (!accepted)
        {
            lock (_automaticClipGate)
            {
                foreach (var region in pending)
                    region.SaveRequested = false;
            }
            return;
        }

        try
        {
            if (!completed.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                lock (_automaticClipGate)
                {
                    foreach (var region in pending)
                    {
                        region.Abandoned = true;
                        region.SaveRequested = false;
                    }
                }
                Log.Warning("AppHost: pending live automatic highlights timed out at stop");
            }
        }
        catch (AggregateException exception)
        {
            Log.Warning(exception, "AppHost: pending live automatic highlights did not settle at stop");
        }
    }
}
