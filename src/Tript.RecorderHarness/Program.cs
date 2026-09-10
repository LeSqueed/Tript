// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;

namespace Tript.RecorderHarness;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: Tript.RecorderHarness <output-path> [duration-seconds] [--multi-track <count>] [--recorder]");
            return 2;
        }

        var outputPath = args[0];
        var durationSeconds = args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 1.0;

        var multiTrackCount = 0;
        var useRecorder = false;
        for (var index = 2; index < args.Length; index++)
        {
            if (args[index] == "--multi-track" && index + 1 < args.Length
                && int.TryParse(args[index + 1], out var count))
            {
                multiTrackCount = count;
            }
            else if (args[index] == "--recorder")
            {
                useRecorder = true;
            }
        }

        try
        {
            return useRecorder
                ? RunRecorder(outputPath, durationSeconds)
                : multiTrackCount > 0
                    ? RunMultiTrack(outputPath, durationSeconds, multiTrackCount)
                    : Run(outputPath, durationSeconds);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.RecorderHarness: {exception}");
            return 3;
        }
    }

    private static void ApplyDiscoveredModulePaths(ObsRuntime runtime)
    {
        var locations = ObsRuntimeLocator.Discover();
        if (!locations.Found)
            throw new InvalidOperationException(
                "No OBS runtime was found; the harness needs a system obs-studio install.");

        var binaryPattern = Path.Combine(locations.ModuleBinaryDir!, "%module%.so");
        var dataPattern = Path.Combine(locations.ModuleDataDir ?? locations.ModuleBinaryDir!, "%module%");
        runtime.AddModulePath(binaryPattern, dataPattern);

        if (locations.CoreDataDir is not null)
            runtime.AddDataPath(locations.CoreDataDir);
        if (locations.LibobsDataDir is not null)
            runtime.AddDataPath(locations.LibobsDataDir);
    }

    private static int RunRecorder(string outputPath, double durationSeconds)
    {
        if (XInitThreads() == 0)
        {
            Console.Error.WriteLine("XInitThreads failed.");
            return 4;
        }

        var display = XOpenDisplay(null);
        if (display == nint.Zero)
        {
            Console.Error.WriteLine("XOpenDisplay returned null; no X server reachable.");
            return 5;
        }

        using var runtime = ObsRuntime.Start(new ObsStartupOptions
        {
            Locale = "en-US",
            NixPlatform = ObsNixPlatform.X11Egl,
            NixPlatformDisplay = display
        });

        var video = new ObsVideoSettings
        {
            BaseWidth = 1280,
            BaseHeight = 720,
            OutputWidth = 1280,
            OutputHeight = 720
        };

        if (runtime.ResetVideo(video) != ObsVideoResetResult.Success)
        {
            Console.Error.WriteLine("obs_reset_video refused the settings.");
            return 6;
        }

        if (!runtime.ResetAudio(new ObsAudioSettings()))
        {
            Console.Error.WriteLine("obs_reset_audio refused the default settings.");
            return 7;
        }

        foreach (var module in new[] { "obs-x264", "obs-ffmpeg", "linux-capture", "image-source", "linux-pulseaudio" })
            runtime.AddSafeModule(module);

        ApplyDiscoveredModulePaths(runtime);
        var report = runtime.LoadAllModules();
        runtime.PostLoadModules();

        if (!report.AllLoaded)
        {
            Console.Error.WriteLine($"Modules failed to load: {string.Join(", ", report.FailedModules)}");
            return 8;
        }

        using var colour = ObsSource.CreatePrivate("color_source", "recorder colour");
        using var session = new ObsRecorderSession(runtime, colour);
        using var recorder = new Tript.Recorder.Recorder(session, RecorderSettings(outputPath));

        if (!recorder.Start(RecorderSettings(outputPath)))
        {
            Console.Error.WriteLine($"The recorder refused the start: {recorder.Snapshot.LastError ?? recorder.Snapshot.LastStopReason?.ToString() ?? "(no reason)"}");
            return 10;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
        {
            if (recorder.Snapshot.State != RecorderState.Recording)
            {
                Console.Error.WriteLine($"The recorder left Recording early: {recorder.Snapshot.LastStopReason}");
                Console.WriteLine($"RESULT:{Describe(recorder.Snapshot.LastStopCode ?? ObsOutputStopCode.Error)}");
                return 1;
            }

            Thread.Sleep(5);
        }

        if (!recorder.Stop())
        {
            Console.Error.WriteLine("The recorder refused the stop.");
            return 11;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (recorder.Snapshot.State != RecorderState.Idle)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The recorder did not return to Idle in time.");

            Thread.Sleep(20);
        }

        if (recorder.Snapshot.LastStopReason != RecorderStopReason.UserRequested)
        {
            Console.Error.WriteLine($"The recording ended with reason {recorder.Snapshot.LastStopReason}.");
            Console.WriteLine($"RESULT:{Describe(recorder.Snapshot.LastStopCode ?? ObsOutputStopCode.Error)}");
            return 1;
        }

        if (!System.IO.File.Exists(outputPath) || new System.IO.FileInfo(outputPath).Length == 0)
        {
            Console.Error.WriteLine("The recording reported success but no non-empty file is on disk.");
            Console.WriteLine("RESULT:FAIL:NO_FILE");
            return 1;
        }

        Console.WriteLine("RESULT:SUCCESS");
        return 0;
    }

    private static ResolvedRecorderSettings RecorderSettings(string outputPath) => new()
    {
        Mode = RecordingMode.Session,
        OutputPath = outputPath,
        ResolutionWidth = 1280,
        ResolutionHeight = 720,
        Fps = 30,
        Encoder = "x264",
        Quality = 12,
        AudioTracks =
        {
            new AudioTrack { Name = "Program", Sources = { new AudioSource { Name = "Program", Kind = AudioSourceKind.Output } } }
        }
    };

    private static int Run(string outputPath, double durationSeconds)
    {
        if (XInitThreads() == 0)
        {
            Console.Error.WriteLine("XInitThreads failed.");
            return 4;
        }

        var display = XOpenDisplay(null);
        if (display == nint.Zero)
        {
            Console.Error.WriteLine("XOpenDisplay returned null; no X server reachable.");
            return 5;
        }

        using var runtime = ObsRuntime.Start(new ObsStartupOptions
        {
            Locale = "en-US",
            NixPlatform = ObsNixPlatform.X11Egl,
            NixPlatformDisplay = display
        });

        var video = new ObsVideoSettings
        {
            BaseWidth = 1280,
            BaseHeight = 720,
            OutputWidth = 1280,
            OutputHeight = 720
        };

        var videoResult = runtime.ResetVideo(video);
        if (videoResult != ObsVideoResetResult.Success)
        {
            Console.Error.WriteLine($"obs_reset_video reported {videoResult}.");
            return 6;
        }

        if (!runtime.ResetAudio(new ObsAudioSettings()))
        {
            Console.Error.WriteLine("obs_reset_audio refused the default settings.");
            return 7;
        }

        foreach (var module in new[] { "obs-x264", "obs-ffmpeg", "linux-capture", "image-source" })
            runtime.AddSafeModule(module);

        ApplyDiscoveredModulePaths(runtime);
        var report = runtime.LoadAllModules();
        runtime.PostLoadModules();

        if (!report.AllLoaded)
        {
            Console.Error.WriteLine($"Modules failed to load: {string.Join(", ", report.FailedModules)}");
            return 8;
        }

        if (!runtime.TryGetVideoHandle(out var videoHandle) || !runtime.TryGetAudioHandle(out var audioHandle))
        {
            Console.Error.WriteLine("The video or audio mix handle could not be obtained.");
            return 9;
        }

        using var outputSettings = new ObsSettings();
        outputSettings.SetString("path", outputPath);
        using var output = ObsOutput.Create("ffmpeg_muxer", "harness recorder", outputSettings);

        using var videoSettings = new ObsSettings();
        videoSettings.SetString("rate_control", "CRF");
        videoSettings.SetInt("crf", 22);
        videoSettings.SetInt("keyint_sec", 1);
        var videoEncoder = ObsEncoder.CreateVideo("obs_x264", "harness video", videoSettings);
        videoEncoder.BindToVideo(videoHandle);

        using var audioSettings = new ObsSettings();
        audioSettings.SetInt("bitrate", 128);
        var audioEncoder = ObsEncoder.CreateAudio("ffmpeg_aac", "harness audio", audioSettings, mixerIndex: 0);
        audioEncoder.BindToAudio(audioHandle);

        output.SetVideoEncoder(videoEncoder);
        output.SetAudioEncoder(audioEncoder, 0);

        var colour = ObsSource.CreatePrivate("color_source", "harness colour");
        runtime.SetOutputSource(0, colour);

        var stop = new TaskCompletionSource<ObsOutputStopEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ObsOutputStopEvent> handler = (_, payload) => stop.TrySetResult(payload);
        output.Stopped += handler;

        try
        {
            if (!output.Start())
            {
                Console.Error.WriteLine($"obs_output_start refused: {output.LastError ?? "(no reason)"}");
                return 10;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds) && !stop.Task.IsCompleted)
                Thread.Sleep(5);

            if (!stop.Task.IsCompleted)
            {
                output.Stop();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (!stop.Task.IsCompleted)
                {
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("The output did not emit its stop signal in time.");

                    Thread.Sleep(20);
                }
            }

            var code = stop.Task.Result.Code;
            if (code != ObsOutputStopCode.Success)
            {
                Console.Error.WriteLine($"The recording ended with stop code {code}.");
                Console.WriteLine($"RESULT:{Describe(code)}");
                return 1;
            }

            if (!System.IO.File.Exists(outputPath) || new System.IO.FileInfo(outputPath).Length == 0)
            {
                Console.Error.WriteLine("The recording reported success but no non-empty file is on disk.");
                Console.WriteLine("RESULT:FAIL:NO_FILE");
                return 1;
            }

            Console.WriteLine("RESULT:SUCCESS");
            return 0;
        }
        finally
        {
            output.Stopped -= handler;
        }
    }

    private static int RunMultiTrack(string outputPath, double durationSeconds, int trackCount)
    {
        if (XInitThreads() == 0)
        {
            Console.Error.WriteLine("XInitThreads failed.");
            return 4;
        }

        var display = XOpenDisplay(null);
        if (display == nint.Zero)
        {
            Console.Error.WriteLine("XOpenDisplay returned null; no X server reachable.");
            return 5;
        }

        using var runtime = ObsRuntime.Start(new ObsStartupOptions
        {
            Locale = "en-US",
            NixPlatform = ObsNixPlatform.X11Egl,
            NixPlatformDisplay = display
        });

        var video = new ObsVideoSettings
        {
            BaseWidth = 1280,
            BaseHeight = 720,
            OutputWidth = 1280,
            OutputHeight = 720
        };

        if (runtime.ResetVideo(video) != ObsVideoResetResult.Success)
        {
            Console.Error.WriteLine("obs_reset_video refused the settings.");
            return 6;
        }

        if (!runtime.ResetAudio(new ObsAudioSettings()))
        {
            Console.Error.WriteLine("obs_reset_audio refused the default settings.");
            return 7;
        }

        foreach (var module in new[] { "obs-x264", "obs-ffmpeg", "linux-capture", "image-source", "linux-pulseaudio" })
            runtime.AddSafeModule(module);

        ApplyDiscoveredModulePaths(runtime);
        var report = runtime.LoadAllModules();
        runtime.PostLoadModules();

        if (!report.AllLoaded)
        {
            Console.Error.WriteLine($"Modules failed to load: {string.Join(", ", report.FailedModules)}");
            return 8;
        }

        if (!runtime.TryGetVideoHandle(out var videoHandle) || !runtime.TryGetAudioHandle(out var audioHandle))
        {
            Console.Error.WriteLine("The video or audio mix handle could not be obtained.");
            return 9;
        }

        using var outputSettings = new ObsSettings();
        outputSettings.SetString("path", outputPath);
        using var output = ObsOutput.Create("ffmpeg_muxer", "harness multi-track", outputSettings);

        using var videoSettings = new ObsSettings();
        videoSettings.SetString("rate_control", "CRF");
        videoSettings.SetInt("crf", 22);
        videoSettings.SetInt("keyint_sec", 1);
        var videoEncoder = ObsEncoder.CreateVideo("obs_x264", "harness video", videoSettings);
        videoEncoder.BindToVideo(videoHandle);
        output.SetVideoEncoder(videoEncoder);

        var tracks = new List<AudioTrack>(trackCount);
        for (var index = 0; index < trackCount; index++)
        {
            tracks.Add(new AudioTrack
            {
                Name = $"Track {index}",
                Sources =
                {
                    new AudioSource { Name = $"Source {index}", Kind = AudioSourceKind.Input, Volume = 1.0f }
                }
            });
        }

        var sink = new ObsAudioRoutingSink(output, audioHandle);
        var routingService = new AudioRoutingService(sink);
        using var routing = routingService.Wire(AudioRoutingPlanner.Plan(tracks));

        var colour = ObsSource.CreatePrivate("color_source", "harness colour");
        runtime.SetOutputSource(0, colour);

        var stop = new TaskCompletionSource<ObsOutputStopEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ObsOutputStopEvent> handler = (_, payload) => stop.TrySetResult(payload);
        output.Stopped += handler;

        try
        {
            if (!output.Start())
            {
                Console.Error.WriteLine($"obs_output_start refused: {output.LastError ?? "(no reason)"}");
                return 10;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds) && !stop.Task.IsCompleted)
                Thread.Sleep(5);

            if (!stop.Task.IsCompleted)
            {
                output.Stop();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (!stop.Task.IsCompleted)
                {
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("The output did not emit its stop signal in time.");

                    Thread.Sleep(20);
                }
            }

            var code = stop.Task.Result.Code;
            if (code != ObsOutputStopCode.Success)
            {
                Console.Error.WriteLine($"The recording ended with stop code {code}.");
                Console.WriteLine($"RESULT:{Describe(code)}");
                return 1;
            }

            if (!System.IO.File.Exists(outputPath) || new System.IO.FileInfo(outputPath).Length == 0)
            {
                Console.Error.WriteLine("The recording reported success but no non-empty file is on disk.");
                Console.WriteLine("RESULT:FAIL:NO_FILE");
                return 1;
            }

            Console.WriteLine("RESULT:SUCCESS");
            return 0;
        }
        finally
        {
            output.Stopped -= handler;
        }
    }

    private static string Describe(ObsOutputStopCode code) => code switch
    {
        ObsOutputStopCode.EncodeError => "ENCODE_ERROR",
        _ => $"FAIL:{(int)code}"
    };

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern nint XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();
}
