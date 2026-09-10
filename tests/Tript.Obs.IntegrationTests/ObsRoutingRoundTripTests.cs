// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

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

        var audio = root.GetProperty("audio");
        Assert.Equal(trackCount, root.GetProperty("audio_track_count").GetInt32());
        Assert.Equal(trackCount, audio.GetArrayLength());

        for (var index = 0; index < trackCount; index++)
            Assert.Equal("aac", audio[index].GetProperty("codec_name").GetString());
    }

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
        }
    }
}
