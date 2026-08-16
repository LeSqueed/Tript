// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Xunit;

namespace Tript.Obs.IntegrationTests;

// The round-trip verification for the audio routing: a real recording made through the routing
// service (spec/recorder.md, "Multi-track audio"). The harness child process wires the audio path
// through AudioRoutingService + ObsAudioRoutingSink — sources routed into mixers, one encoder per
// mixer assigned to the matching output slot — records, and the probe proves the file actually
// carries the expected number of audio tracks. This is the join between the settings model (how many
// tracks were configured) and the binding (how many tracks are on disk).
//
// The recording itself runs in the harness child process for the same measured reason as every
// recording test: the ffmpeg_muxer plugin spawns its obs-ffmpeg-mux helper next to the *actual*
// binary, and under dotnet test that binary is dotnet. See ObsRecorderHarnessDriver.
public sealed class ObsRoutingRoundTripTests
{
    [Fact]
    public void ARecordingRoutedThroughTheService_HasOneAudioTrackPerPlannedTrack()
    {
        Assert.True(ObsRecorderHarnessDriver.EnsureHelperPresent(),
            "The obs-ffmpeg-mux helper is not next to the harness and could not be deployed.");

        const int trackCount = 3;
        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "multi-track.mp4");

        var (verdict, exitCode) = ObsRecorderHarnessDriver.RunMultiTrack(file, durationSeconds: 1.0, trackCount);
        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);
        Assert.Equal(0, exitCode);

        var probe = ProbeMedia(file);
        Assert.True(probe is not null, $"probe-media.sh produced no parseable output for {file}.");
        var root = probe!.RootElement;

        // The file carries one audio stream per track the routing was given.
        var audio = root.GetProperty("audio");
        Assert.Equal(trackCount, root.GetProperty("audio_track_count").GetInt32());
        Assert.Equal(trackCount, audio.GetArrayLength());

        // Every stream is the AAC the sink's track encoders create.
        for (var index = 0; index < trackCount; index++)
            Assert.Equal("aac", audio[index].GetProperty("codec_name").GetString());
    }

    // The number of audio tracks the file carries is bounded by the same bound the planner enforces:
    // six tracks is the maximum, and the file reflects it.
    [Fact]
    public void SixTracks_TheMaximumThePlannerAllows_ProducesSixAudioTracks()
    {
        Assert.True(ObsRecorderHarnessDriver.EnsureHelperPresent(),
            "The obs-ffmpeg-mux helper is not next to the harness and could not be deployed.");

        const int trackCount = 6;
        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "six-track.mp4");

        var (verdict, exitCode) = ObsRecorderHarnessDriver.RunMultiTrack(file, durationSeconds: 1.0, trackCount);
        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);
        Assert.Equal(0, exitCode);

        var probe = ProbeMedia(file);
        Assert.True(probe is not null, $"probe-media.sh produced no parseable output for {file}.");
        var root = probe!.RootElement;
        Assert.Equal(trackCount, root.GetProperty("audio_track_count").GetInt32());

        Cleanup(directory);
    }

    private static string CreateRecordingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tript-routing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    // Runs the spec's differential probe against a recording. Returns null when the probe could not
    // be found or produced no parseable output.
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
