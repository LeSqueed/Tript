// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

// The round-trip verification for the audio routing: a real recording made through the routing
// service. The harness child process wires the audio path
// through AudioRoutingService + ObsAudioRoutingSink — sources routed into mixers, one encoder per
// mixer assigned to the matching output slot — records, and the probe proves the file actually
// carries the expected number of audio tracks.
public sealed class ObsRoutingRoundTripTests
{
    [SkippableFact]
    public void ARecordingRoutedThroughTheService_HasOneAudioTrackPerPlannedTrack()
    {
        ObsRecorderHarnessDriver.RequireHelper();

        const int trackCount = 3;
        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "multi-track.mp4");

        var (verdict, exitCode) = ObsRecorderHarnessDriver.RunMultiTrack(file, durationSeconds: 1.0, trackCount);
        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);
        Assert.Equal(0, exitCode);

        using var probe = ProbeMediaScript.Run(file);
        var root = probe.RootElement;

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
    [SkippableFact]
    public void SixTracks_TheMaximumThePlannerAllows_ProducesSixAudioTracks()
    {
        ObsRecorderHarnessDriver.RequireHelper();

        const int trackCount = 6;
        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "six-track.mp4");

        var (verdict, exitCode) = ObsRecorderHarnessDriver.RunMultiTrack(file, durationSeconds: 1.0, trackCount);
        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);
        Assert.Equal(0, exitCode);

        using var probe = ProbeMediaScript.Run(file);
        var root = probe.RootElement;
        Assert.Equal(trackCount, root.GetProperty("audio_track_count").GetInt32());

        Cleanup(directory);
    }

    private static string CreateRecordingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tript-routing-{Guid.NewGuid():N}");
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
