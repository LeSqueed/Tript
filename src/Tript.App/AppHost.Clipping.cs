// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using System.Diagnostics;
using Serilog;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.App.Models;
using Tript.Core;
using Tript.Detection;
using Tript.GameDiscovery;
using Tript.Media;
using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;
#if TRIPT_TRAINING
using Tript.App.Training;
#endif
using RecorderStateMachine = Tript.Recorder.Recorder;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App;

internal sealed partial class AppHost
{
    // ---- clipping ----

    internal void CreateAutomaticClips(CreateAutomaticClipsParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.FilePath))
        {
            PushError("No recording was selected for automatic highlights.");
            return;
        }

        var sourcePath = ContentServer.ResolveWithinRoot(EffectiveRoot, parameters.FilePath);
        if (sourcePath is null || !File.Exists(sourcePath)
            || !TopLevelDirectory(parameters.FilePath.Replace('\\', '/'))
                .Equals("sessions", StringComparison.Ordinal))
        {
            PushError("That recording is not inside the sessions library.");
            return;
        }

        var metadata = _metadata.Load(Path.GetFileName(sourcePath));
        if (metadata is null)
        {
            PushError("That recording has no event metadata, so no automatic highlights can be created.");
            return;
        }

        var candidates = metadata.Bookmarks
            .Where(IsAutomaticClipCandidate)
            .ToList();
        if (candidates.Count == 0)
        {
            PushError("That recording has no positive events to turn into highlights.");
            return;
        }

        var sourceSessionPath = Path.GetRelativePath(EffectiveRoot, sourcePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!QueueAutomaticClips(sourcePath, sourceSessionPath, candidates, metadata.GameId))
            PushError("Automatic highlights are already being created for another recording.");
    }

    // What counts as an automatic-highlight candidate: an event definition that opted into clips,
    // or a legacy bookmark type that predates the per-definition flag. Shared by the clip command
    // and the library listing so "has highlights to create" never disagrees with "creates them".
    private static bool IsAutomaticClipCandidate(Bookmark bookmark) =>
        bookmark.IsAutomaticClipCandidate == true
        || (bookmark.IsAutomaticClipCandidate is null
            && bookmark.Type.IsIncludedInHighlights());

    internal void ToggleAutomaticClipPause()
    {
        lock (_automaticClipGate)
        {
            if (_automaticClipJob is null)
                return;
            if (_backgroundWorkSuspendedForRecording)
                return;

            _automaticClipJob.PausedByUser = !_automaticClipJob.PausedByUser;
            Monitor.PulseAll(_automaticClipGate);
        }

        PushState(IsRecording, CurrentGameId);
        PushContent();
    }

    private bool QueueAutomaticClips(string sourcePath, string sourceSessionPath,
        IReadOnlyList<Bookmark> bookmarks, string? gameId = null)
    {
        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(_settingsStore.Load(), gameId);
        var regions = AutomaticClipPlanner.Plan(bookmarks.Select(bookmark => bookmark.Time), before, after);
        if (regions.Count == 0)
            return false;

        var job = new AutomaticClipJob
        {
            SourceSessionPath = sourceSessionPath,
            Total = regions.Count,
        };
        lock (_automaticClipGate)
        {
            if (_automaticClipJob is not null)
                return false;
            _automaticClipJob = job;
        }

        PushState(IsRecording, CurrentGameId);
        PushContent();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _clipEngine ??= BuildClipEngine();
                var sourceBaseName = Path.GetFileNameWithoutExtension(sourcePath);
                var outputDirectory = HighlightsDirectoryForSource(sourcePath);
                var failures = 0;

                for (var index = 0; index < regions.Count; index++)
                {
                    try
                    {
                        lock (_automaticClipGate)
                        {
                            while (ReferenceEquals(_automaticClipJob, job)
                                && (job.PausedByUser || _backgroundWorkSuspendedForRecording))
                                Monitor.Wait(_automaticClipGate);
                        }

                        if (!ReferenceEquals(_automaticClipJob, job))
                            return;

                        var region = regions[index];
                        var outputPath = Path.Combine(outputDirectory,
                            $"{sourceBaseName}-highlight-{index + 1}-{Guid.NewGuid():N}.mp4");
                        var results = _clipEngine.CreateClips(new ClipRequest
                        {
                            OperationId = $"automatic-{Guid.NewGuid():N}",
                            SourcePath = sourcePath,
                            Regions = [region],
                            Mode = ClipMode.Combine,
                            OutputPath = outputPath,
                            EncoderFamily = "libx264",
                            PreferStreamCopy = true,
                        });

                        foreach (var result in results)
                        {
                            _clipTitles.SaveAutomatic(Path.GetFileName(result), sourceSessionPath,
                                region.Start.TotalSeconds, region.End.TotalSeconds);
                        }
                        AttachGameToClips(results, sourceSessionPath);

                        lock (_automaticClipGate)
                            job.Completed++;
                        PushState(IsRecording, CurrentGameId);
                    }
                    catch (Exception exception)
                    {
                        failures++;
                        Log.Error(exception, "AppHost: automatic highlight {Index} failed for {SourcePath}",
                            index + 1, sourcePath);
                    }
                }

                if (failures > 0)
                    PushError($"{failures} automatic highlight{(failures == 1 ? "" : "s")} could not be created.");
            }
            catch (Exception exception)
            {
                Log.Error(exception, "AppHost: automatic highlight creation failed for {SourcePath}", sourcePath);
                PushError($"Automatic highlights could not be created: {exception.Message}");
            }
            finally
            {
                lock (_automaticClipGate)
                {
                    if (ReferenceEquals(_automaticClipJob, job))
                        _automaticClipJob = null;
                    Monitor.PulseAll(_automaticClipGate);
                }

                PushState(IsRecording, CurrentGameId);
                PushContent();
            }
        });
        return true;
    }

    // Reports a clip that could not even be started — a source path that does not resolve inside the
    // recording root. It reuses the importProgress "error" frame the engine's own failures use, which
    // is what the clip dialog renders its failure state from.
    internal void PushClipError(string operationId, string message)
    {
        _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
        {
            id = operationId,
            status = "error",
            error = message,
        }, Wire.Options));
    }

    internal void CreateClip(ClipRequest request)
    {
        _clipEngine ??= BuildClipEngine();

        // Never block synchronously: the ffmpeg run is off the IPC thread and progress arrives as
        // importProgress messages.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
                {
                    id = request.OperationId,
                    status = "importing",
                }, Wire.Options));

                var results = _clipEngine.CreateClips(request);

                // Persisted against every produced file (one in combine mode, one per region in separate mode).
                // A failed write is logged inside the store and does not fail the clip.
                if (!string.IsNullOrWhiteSpace(request.Title))
                {
                    foreach (var result in results)
                        _clipTitles.Save(Path.GetFileName(result), request.Title);
                }
                if (!string.IsNullOrWhiteSpace(request.SourceSessionPath))
                {
                    foreach (var result in results)
                        _clipTitles.SaveSourceSession(Path.GetFileName(result), request.SourceSessionPath);
                    AttachGameToClips(results, request.SourceSessionPath);
                }

                _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
                {
                    id = request.OperationId,
                    status = "done",
                    content = new ContentItem
                    {
                        ContentType = "clip",
                        FileName = Path.GetFileName(results[0]),
                        FilePath = Path.GetRelativePath(EffectiveRoot, results[0]).Replace(Path.DirectorySeparatorChar, '/'),
                        Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title,
                    },
                }, Wire.Options));

                // A clip completed: the catalogue changed.
                PushContent();
            }
            catch (Exception exception)
            {
                _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
                {
                    id = request.OperationId,
                    status = "error",
                    error = exception.Message,
                }, Wire.Options));
            }
        });
    }

    internal void ConvertToSdr(ConvertToSdrParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.FilePath))
            return;

        var operationId = string.IsNullOrWhiteSpace(parameters.Id)
            ? $"sdr-{Guid.NewGuid():N}"
            : parameters.Id.Trim();
        if (BackgroundWorkSuspendedForRecording)
        {
            PushConversionProgress(operationId, "error", "SDR conversion is unavailable while recording.");
            return;
        }

        var source = ContentServer.ResolveWithinRoot(EffectiveRoot, parameters.FilePath);
        var relative = source is null ? null
            : Path.GetRelativePath(EffectiveRoot, source).Replace(Path.DirectorySeparatorChar, '/');
        var topLevel = relative is null ? string.Empty : TopLevelDirectory(relative);
        if (source is null || !File.Exists(source) || topLevel is not ("clips" or "highlights"))
        {
            PushConversionProgress(operationId, "error", "That file is not a clip or highlight inside the recording folder.");
            return;
        }

        MediaProbe? probe;
        try
        {
            probe = LibraryProbe;
            if (probe is null)
                throw new InvalidOperationException("Media tools are unavailable.");
            var info = probe.Probe(source);
            if (!info.IsHdr)
                throw new InvalidOperationException("The selected file is already SDR.");
            if (!double.IsFinite(info.DurationSeconds) || info.DurationSeconds <= 0)
                throw new InvalidOperationException("The selected file has no usable duration.");

            string output;
            lock (_sdrConversionGate)
            {
                if (!_sdrConversions.Add(source))
                {
                    PushConversionProgress(operationId, "error", "An SDR conversion is already running for this file.");
                    return;
                }
                output = NextSdrPath(source);
                _reservedClipOutputs.Add(output);
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    PushConversionProgress(operationId, "importing", null);
                    _clipEngine ??= BuildClipEngine();
                    var results = _clipEngine.CreateClips(new ClipRequest
                    {
                        OperationId = operationId,
                        SourcePath = source,
                        Regions = [ClipRegion.FromSeconds(0, info.DurationSeconds)],
                        Mode = ClipMode.Combine,
                        OutputPath = output,
                        EncoderFamily = "libx264",
                        ForceSdr = true,
                    });
                    var converted = results[0];
                    if (!_clipTitles.SaveConvertedFrom(Path.GetFileName(source), Path.GetFileName(converted)))
                        throw new InvalidOperationException("The SDR file was created, but its metadata could not be saved.");

                    PushConversionProgress(operationId, "done", new ContentItem
                    {
                        ContentType = parameters.ContentType,
                        FileName = Path.GetFileName(converted),
                        FilePath = Path.GetRelativePath(EffectiveRoot, converted).Replace(Path.DirectorySeparatorChar, '/'),
                        IsHdr = false,
                    });
                    PushContent();
                }
                catch (Exception exception)
                {
                    PushConversionProgress(operationId, "error", exception.Message);
                    try { if (File.Exists(output)) File.Delete(output); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                finally
                {
                    lock (_sdrConversionGate)
                    {
                        _sdrConversions.Remove(source);
                        _reservedClipOutputs.Remove(output);
                    }
                }
            });
        }
        catch (Exception exception)
        {
            PushConversionProgress(operationId, "error", exception.Message);
        }
    }

    private void PushConversionProgress(string id, string status, object? content)
    {
        if (status == "error")
        {
            PushClipError(id, content?.ToString() ?? "SDR conversion failed.");
            return;
        }

        _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new { id, status, content }, Wire.Options));
    }

    private string NextSdrPath(string source)
    {
        var directory = Path.GetDirectoryName(source)!;
        var stem = Path.GetFileNameWithoutExtension(source) + "-sdr";
        var candidate = Path.Combine(directory, stem + ".mp4");
        for (var suffix = 2; File.Exists(candidate) || _reservedClipOutputs.Contains(candidate); suffix++)
            candidate = Path.Combine(directory, $"{stem}-{suffix}.mp4");
        return candidate;
    }

    internal bool BackgroundWorkSuspendedForRecording
    {
        get
        {
            lock (_automaticClipGate)
                return _backgroundWorkSuspendedForRecording;
        }
    }

    private void SetBackgroundWorkSuspendedForRecording(bool suspended)
    {
        lock (_automaticClipGate)
        {
            _backgroundWorkSuspendedForRecording = suspended;
            Monitor.PulseAll(_automaticClipGate);
        }
    }

    private IClipEngine BuildClipEngine()
    {
        var (ffmpeg, ffprobe) = _libraryTools.Value
            ?? throw new FfmpegNotFoundException("ffmpeg was not found. Install ffmpeg and ensure it is on PATH.");
        return new ClipEngine(ffmpeg, new MediaProbe(ffprobe));
    }
}
