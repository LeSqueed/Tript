// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;

namespace Tript.RecorderHarness;

// The recorder harness: a real native executable that owns an OBS context immersive enough to
// record a file. It exists because the ffmpeg_muxer plugin spawns its obs-ffmpeg-mux helper next to
// the *actual binary* of the process (os_get_executable_path_ptr resolves /proc/self/exe) — and the
// test host that drives the integration tests is dotnet, which cannot have a helper dropped beside
// it. Running the recording in this harness, whose apphost directory carries the helper, is how the
// integration tests record a real file from under dotnet test.
//
// The harness is deliberately tiny and its contract is a single line of stdout, printed last:
//
//   RESULT:SUCCESS                the recording was made and its stop signal reported success
//   RESULT:ENCODE_ERROR           the stop signal reported ENCODE_ERROR (the muxer helper died)
//   RESULT:FAIL:<code>            the stop signal reported another failure code
//
// plus a non-zero exit status for anything that failed before a stop signal existed. The parent
// asserts on the result line, the file on disk, and the probe. The parent drives the control-plane;
// this process exists so the data plane (the muxer) has a real executable to hang a helper next to.
internal static class Program
{
    private static int Main(string[] args)
    {
        // Usage: Tript.RecorderHarness <output-path> [duration-seconds] [--recorder]
        // Both real arguments the parent passes. The harness records a colour source on channel 0
        // through x264 and aac, waits duration-seconds (default 1.0), stops, and exits.
        //
        // With --recorder, the recording is driven through the T3 recorder state machine
        // (Recorder over ObsRecorderSession) instead of the raw binding: Start, run, Stop, and the
        // stop signal completes the transition back to Idle. The parent asserts the recorder's
        // snapshot and the file, which is how the state machine is verified against a real muxer.
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: Tript.RecorderHarness <output-path> [duration-seconds] [--recorder]");
            return 2;
        }

        var outputPath = args[0];
        var durationSeconds = args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 1.0;

        var useRecorder = args.Skip(2).Contains("--recorder");

        try
        {
            return useRecorder
                ? RunRecorder(outputPath, durationSeconds)
                : Run(outputPath, durationSeconds);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.RecorderHarness: {exception}");
            return 3;
        }
    }

    // The recorder-driven path: a real runtime, a colour source the app owns, and the T3 recorder
    // state machine owning the output. Start (Session mode), run, Stop, and let the stop signal
    // complete the transition. The parent asserts the state machine's snapshot and the file — the
    // recorder's Idle -> Recording -> Stopping -> Idle round-trip against a real muxer.
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

        foreach (var module in new[] { "obs-x264", "obs-ffmpeg", "linux-capture", "image-source" })
            runtime.AddSafeModule(module);

        runtime.AddModulePath("/usr/lib/obs-plugins/%module%.so", "/usr/share/obs/obs-studio/plugins/%module%/data");
        var report = runtime.LoadAllModules();
        runtime.PostLoadModules();

        if (!report.AllLoaded)
        {
            Console.Error.WriteLine($"Modules failed to load: {string.Join(", ", report.FailedModules)}");
            return 8;
        }

        // The colour source is the app's own source; the recorder borrows it via the session.
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

    // The resolved settings the recorder consumes: Session mode, the harness's 1280x720 display,
    // and the recorded file's path. The alpha recorder records Session only.
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
        // Same display plumbing the integration tests use: libobs cannot discover the display server
        // on Linux, so the process opens an X display itself and hands the pointer over.
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

        // The same safe module list the integration tests use: an allowlist that keeps headless
        // plugin loading from pulling in frontend-tools, which aborts the process.
        foreach (var module in new[] { "obs-x264", "obs-ffmpeg", "linux-capture", "image-source" })
            runtime.AddSafeModule(module);

        runtime.AddModulePath("/usr/lib/obs-plugins/%module%.so", "/usr/share/obs/obs-studio/plugins/%module%/data");
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

        // A static colour source on channel 0 is what makes the recording non-empty and
        // deterministic, exactly as the integration fixture does.
        var colour = ObsSource.CreatePrivate("color_source", "harness colour");
        runtime.SetOutputSource(0, colour);

        // The stop signal is subscribed *before* start, not at stop time. The muxer can end on its
        // own — a killed helper reports ENCODE_ERROR mid-recording — and that signal would be missed
        // by a handler attached only when Stop() is called.
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

            // Let the recording run for the requested time, long enough for keyframes.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds) && !stop.Task.IsCompleted)
                Thread.Sleep(5);

            // The output may have already ended (stop signal during the wait); if not, ask it to stop.
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
