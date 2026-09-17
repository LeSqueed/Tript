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
    internal void CreateAutomaticClips(CreateAutomaticClipsParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.FilePath))
        {
            PushError("No recording was selected for automatic highlights.");
            return;
        }

        var sourcePath = ContentServer.ResolveWithinRoot(EffectiveRoot, parameters.FilePath);
        if (sourcePath is null || !File.Exists(sourcePath)
            || !ContentLayout.IsSessionPath(parameters.FilePath.Replace('\\', '/')))
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
            .Where(AutomaticClipCandidates.Includes)
            .ToList();
        if (candidates.Count == 0)
        {
            PushError("That recording has no positive events to turn into highlights.");
            return;
        }

        var sourceSessionPath = RelativeToRoot(sourcePath);
        if (!QueueAutomaticClips(sourcePath, sourceSessionPath, candidates, metadata.GameId))
            PushError("Automatic highlights are already being created for another recording.");
    }

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
        _clipQueue.Enqueue(request);
    }

    private void ReportClipFailure(ClipRequest request, Exception exception) =>
        _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
        {
            id = request.OperationId,
            status = "error",
            error = exception.Message,
        }, Wire.Options));

    private void ProcessClip(ClipRequest request)
    {
        _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
        {
            id = request.OperationId,
            status = "importing",
        }, Wire.Options));

        var outputs = _clipEngine!.CreateClipOutputs(request);
        var results = outputs.Select(output => output.Path).ToList();

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            foreach (var result in results)
                _clipTitles.Save(Path.GetFileName(result), request.Title);
        }
        if (!string.IsNullOrWhiteSpace(request.SourceSessionPath))
        {
            foreach (var output in outputs)
            {
                _clipTitles.SaveSourceSession(Path.GetFileName(output.Path), request.SourceSessionPath,
                    output.Regions
                        .Select(region => new ClipSourceSpan
                        {
                            Start = region.Start.TotalSeconds,
                            End = region.End.TotalSeconds,
                        })
                        .ToList());
            }
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
                FilePath = RelativeToRoot(results[0]),
                Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title,
            },
        }, Wire.Options));

        PushContent();
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
        if (source is null || !File.Exists(source) || !ContentLayout.IsClipPath(RelativeToRoot(source)))
        {
            PushConversionProgress(operationId, "error", "That file is not a clip or highlight inside the recording folder.");
            return;
        }

        MediaProbe? probe;
        try
        {
            probe = _libraryProbe.Probe;
            if (probe is null)
                throw new InvalidOperationException("Media tools are unavailable.");
            var info = probe.Probe(source);
            if (!info.IsHdr)
                throw new InvalidOperationException("The selected file is already SDR.");
            if (!double.IsFinite(info.DurationSeconds) || info.DurationSeconds <= 0)
                throw new InvalidOperationException("The selected file has no usable duration.");

            if (!_sdrOutputs.TryReserve(source, out var output))
            {
                PushConversionProgress(operationId, "error", "An SDR conversion is already running for this file.");
                return;
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
                        FilePath = RelativeToRoot(converted),
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
                    _sdrOutputs.Release(source, output);
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

        _maintenance.SetRecording(suspended);
    }

    private IClipEngine BuildClipEngine()
    {
        var (ffmpeg, ffprobe) = _libraryTools.Value
            ?? throw new FfmpegNotFoundException("ffmpeg was not found. Install ffmpeg and ensure it is on PATH.");
        return new ClipEngine(ffmpeg, new MediaProbe(ffprobe));
    }

    private sealed class AutomaticClipJob
    {
        internal required string SourceSessionPath { get; init; }

        internal required int Total { get; init; }

        internal int Completed { get; set; }

        internal bool PausedByUser { get; set; }
    }
}
