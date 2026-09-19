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
    private void RememberAutomaticClipBookmark(Bookmark bookmark)
    {
        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(_settingsStore.Load(), _currentGameId);
        bookmark.IsAutomaticClipCandidate = true;
        if (_liveHighlights.Remember(bookmark, before, after) is not { } schedule)
            return;

        _liveHighlights.Track(SaveLiveAutomaticHighlightWhenReady(schedule.Region, schedule.Token));
    }

    private async Task SaveLiveAutomaticHighlightWhenReady(LiveHighlightRegion region,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var (before, _) = SettingsResolver.ResolveAutomaticClipWindow(_settingsStore.Load(), _currentGameId);
                if (_liveHighlights.TimeUntilDue(region, before) is not { } delay)
                    return;

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                var claim = _liveHighlights.TryClaim(region, before);
                if (claim == LiveHighlightClaim.Stop)
                    return;
                if (claim == LiveHighlightClaim.NotYet)
                    continue;

                var recorder = _recorder;
                var sourcePath = _activeSessionPath;
                if (recorder is null || sourcePath is null)
                    return;

                var sourceSessionPath = RelativeToRoot(sourcePath);
                var replayDirectory = ReplayScratchDirectory();
                var saveElapsed = _liveHighlights.ElapsedSeconds;
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
                    _liveHighlights.Release([region]);
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
            _liveHighlights.Abandon([region]);
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
            if (!File.Exists(replayPath) || _liveHighlights.IsAbandoned(region))
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

            if (_liveHighlights.IsAbandoned(region))
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

            _liveHighlights.MarkSaved(region);
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

    private void StopLiveAutomaticHighlights()
    {
        _liveHighlights.Stop();
        SavePendingLiveHighlightsAtStop();
    }

    private void SavePendingLiveHighlightsAtStop()
    {
        var recorder = _recorder;
        var sourcePath = _activeSessionPath;
        if (recorder is null || sourcePath is null)
            return;

        var pending = _liveHighlights.ClaimUnsaved();
        if (pending.Count == 0)
            return;

        var sourceSessionPath = RelativeToRoot(sourcePath);
        var replayDirectory = ReplayScratchDirectory();
        var saveElapsed = _liveHighlights.ElapsedSeconds;
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
            _liveHighlights.Release(pending);
            return;
        }

        try
        {
            if (!completed.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                _liveHighlights.Abandon(pending);
                Log.Warning("AppHost: pending live automatic highlights timed out at stop");
            }
        }
        catch (AggregateException exception)
        {
            Log.Warning(exception, "AppHost: pending live automatic highlights did not settle at stop");
        }
    }
}
