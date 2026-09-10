// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsOutputRecordingTests
{
    private const string FfmpegMuxerId = "ffmpeg_muxer";

    [SkippableFact]
    public void ARecording_ProducesARealFileOnDisk()
    {
        ObsRecorderHarnessDriver.RequireHelper();

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "recording.mp4");

        var (verdict, exitCode) = ObsRecorderHarnessDriver.Run(file, durationSeconds: 1.0);

        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);
        Assert.Equal(0, exitCode);
        var onDisk = Assert.Single(Directory.GetFiles(directory, "recording.mp4"));
        Assert.True(new FileInfo(onDisk).Length > 0, "The recording is empty on disk.");

        Cleanup(directory);
    }

    [SkippableFact]
    public void TheRecording_IsAProbeableMp4WithH264VideoAndAacAudio()
    {
        ObsRecorderHarnessDriver.RequireHelper();

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "recording.mp4");

        var (verdict, _) = ObsRecorderHarnessDriver.Run(file, durationSeconds: 1.0);
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

    [SkippableFact]
    public void TheRecording_HasAnAudioTrackEvenThoughTheSourceIsSilent()
    {
        ObsRecorderHarnessDriver.RequireHelper();

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "recording.mp4");

        var (verdict, _) = ObsRecorderHarnessDriver.Run(file, durationSeconds: 0.5);
        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);

        using var probe = ProbeMediaScript.Run(file);
        var root = probe.RootElement;
        var audio = root.GetProperty("audio");
        Assert.Equal(1, root.GetProperty("audio_track_count").GetInt32());
        Assert.Equal("aac", audio[0].GetProperty("codec_name").GetString());

        Assert.Equal(48000, int.Parse(audio[0].GetProperty("sample_rate").GetString()!, System.Globalization.CultureInfo.InvariantCulture));

        Cleanup(directory);
    }

    [SkippableFact]
    public void TheRecording_CarriesItsColourDescription()
    {
        ObsRecorderHarnessDriver.RequireHelper();

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "recording.mp4");

        var (verdict, _) = ObsRecorderHarnessDriver.Run(file, durationSeconds: 0.5);
        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);

        using var probe = ProbeMediaScript.Run(file);
        var video = probe.RootElement.GetProperty("video");
        foreach (var field in new[] { "color_transfer", "color_primaries", "color_space", "color_range" })
        {
            var value = video.GetProperty(field).GetString();
            Assert.True(
                value is not null && value != "unspecified",
                $"{field} was left unspecified. Colour description was:\n{video}");
        }

        Cleanup(directory);
    }

    [SkippableFact]
    public void ABadPath_IsRefusedSynchronouslyWithANamedReason()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var settings = new ObsSettings();
        settings.SetString("path", Path.Combine(Path.GetTempPath(), $"no-such-tript-dir-{Guid.NewGuid():N}", "out.mp4"));
        using var output = WithEncodersWired(session, ObsOutput.Create(FfmpegMuxerId, "bad path", settings));

        Assert.False(output.Start());
        Assert.NotNull(output.LastError);
        Assert.NotEqual(string.Empty, output.LastError);
    }

    [SkippableFact]
    public async Task AKilledMuxerHelper_DoesNotLookLikeASuccessfulRecording()
    {
        ObsRecorderHarnessDriver.RequireHelper();

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "killed.mp4");

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ObsRecorderHarnessDriver.HarnessPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(file);
        startInfo.ArgumentList.Add("60");

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();

        await Task.Delay(TimeSpan.FromSeconds(2));
        ObsRecorderHarnessDriver.KillMuxerHelper();

        var exited = await Task.Run(() => process.WaitForExit(TimeSpan.FromSeconds(60)));
        Assert.True(exited, "The harness did not exit after its muxer helper was killed.");
        ObsRecorderHarnessDriver.RequireHarnessFoundADisplay(process.ExitCode);

        var stdout = await stdoutTask;
        var resultLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("RESULT:", StringComparison.Ordinal));
        Assert.NotNull(resultLine);

        using var probe = ProbeMediaScript.Run(file);
        Assert.True(IsUnparseable(probe),
            "The killed recording was expected to be unparseable, but ffprobe read it as a playable MP4.");

        Cleanup(directory);
    }

    private static ObsOutput WithEncodersWired(ObsSession session, ObsOutput output)
    {
        session.Runtime.TryGetVideoHandle(out var video);
        session.Runtime.TryGetAudioHandle(out var audio);

        using var videoSettings = new ObsSettings();
        videoSettings.SetString("rate_control", "CRF");
        videoSettings.SetInt("crf", 22);
        videoSettings.SetInt("keyint_sec", 1);
        var videoEncoder = ObsEncoder.CreateVideo("obs_x264", $"{output.Name} video", videoSettings);
        videoEncoder.BindToVideo(video);

        using var audioSettings = new ObsSettings();
        audioSettings.SetInt("bitrate", 128);
        var audioEncoder = ObsEncoder.CreateAudio("ffmpeg_aac", $"{output.Name} audio", audioSettings, mixerIndex: 0);
        audioEncoder.BindToAudio(audio);

        output.SetVideoEncoder(videoEncoder);
        output.SetAudioEncoder(audioEncoder, 0);

        _ = videoEncoder;
        _ = audioEncoder;
        return output;
    }

    private static bool IsUnparseable(JsonDocument probe)
    {
        var root = probe.RootElement;
        if (!root.TryGetProperty("video", out var video))
            return true;
        if (!video.TryGetProperty("codec_name", out var codec) || codec.GetString() is null)
            return true;
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object)
            return true;
        if (!format.TryGetProperty("format_name", out var formatName))
            return true;

        return false;
    }

    private static string CreateRecordingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tript-recording-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WaitForSeconds(double seconds)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(seconds))
            Thread.Sleep(5);
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
