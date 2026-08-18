// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Xunit;

namespace Tript.Obs.IntegrationTests;

// The recorder state machine against a real muxer. The unit tests prove the transitions with a fake
// output; this proves the state machine actually owns a recording that lands on disk — Start, run,
// Stop, the stop signal completing the transition back to Idle, and a real, probeable file.
public sealed class RecorderStateMachineRecordingTests
{
    [Fact]
    public void TheRecorder_ProducesARealFileOnDisk()
    {
        Assert.True(ObsRecorderHarnessDriver.EnsureHelperPresent(),
            "The obs-ffmpeg-mux helper is not next to the harness and could not be deployed.");

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "recording.mp4");

        var (verdict, exitCode) = ObsRecorderHarnessDriver.RunRecorder(file, durationSeconds: 1.0);

        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);
        Assert.Equal(0, exitCode);
        var onDisk = Assert.Single(Directory.GetFiles(directory, "recording.mp4"));
        Assert.True(new FileInfo(onDisk).Length > 0, "The recording is empty on disk.");

        Cleanup(directory);
    }

    // The state machine's stop is what the harness reports: a success verdict means the recorder
    // returned to Idle with a UserRequested stop reason and the muxer reported Success. The file is
    // the other half — the output actually wrote video and audio, not just a header.
    [Fact]
    public void TheRecorder_StopsWithTheCleanReasonAndTheFileIsProbeable()
    {
        Assert.True(ObsRecorderHarnessDriver.EnsureHelperPresent(),
            "The obs-ffmpeg-mux helper is not next to the harness and could not be deployed.");

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "recording.mp4");

        var (verdict, _) = ObsRecorderHarnessDriver.RunRecorder(file, durationSeconds: 1.0);
        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);

        var probe = ProbeMedia(file);
        Assert.True(probe is not null, $"probe-media.sh produced no parseable output for {file}.");
        var root = probe!.RootElement;
        var video = root.GetProperty("video");
        var audio = root.GetProperty("audio");
        var format = root.GetProperty("format");
        Assert.Equal("h264", video.GetProperty("codec_name").GetString());
        Assert.Equal(1280, video.GetProperty("width").GetInt32());
        Assert.Equal(720, video.GetProperty("height").GetInt32());
        Assert.Equal(1, root.GetProperty("audio_track_count").GetInt32());
        Assert.Equal("aac", audio[0].GetProperty("codec_name").GetString());
        Assert.Contains("mp4", format.GetProperty("format_name").GetString(), StringComparison.Ordinal);

        Cleanup(directory);
    }

    // ---- helpers (the same probe and cleanup the raw-binding recording tests use) ----

    private static JsonDocument? ProbeMedia(string file)
    {
        var script = "/home/squeed/Projects/reference-product-separation/scripts/probe-media.sh";
        if (!File.Exists(script))
            return null;

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = script,
            ArgumentList = { file },
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
            return null;

        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30));

        try
        {
            return JsonDocument.Parse(stdout);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CreateRecordingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tript-recorder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Cleanup(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover recording in the temp directory is not worth failing a green suite over.
        }
    }
}
