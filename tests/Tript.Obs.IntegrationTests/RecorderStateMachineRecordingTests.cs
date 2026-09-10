// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class RecorderStateMachineRecordingTests
{
    [SkippableFact]
    public void TheRecorder_ProducesARealFileOnDisk()
    {
        ObsRecorderHarnessDriver.RequireHelper();

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "recording.mp4");

        var (verdict, exitCode) = ObsRecorderHarnessDriver.RunRecorder(file, durationSeconds: 1.0);

        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);
        Assert.Equal(0, exitCode);
        var onDisk = Assert.Single(Directory.GetFiles(directory, "recording.mp4"));
        Assert.True(new FileInfo(onDisk).Length > 0, "The recording is empty on disk.");

        Cleanup(directory);
    }

    [SkippableFact]
    public void TheRecorder_StopsWithTheCleanReasonAndTheFileIsProbeable()
    {
        ObsRecorderHarnessDriver.RequireHelper();

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "recording.mp4");

        var (verdict, _) = ObsRecorderHarnessDriver.RunRecorder(file, durationSeconds: 1.0);
        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);

        using var probe = ProbeMediaScript.Run(file);
        var root = probe.RootElement;
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
        }
    }
}
