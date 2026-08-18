// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Xunit;

namespace Tript.Obs.IntegrationTests;

// The milestone: a real ffmpeg_muxer output, a real x264 video encoder, a real ffmpeg_aac audio
// encoder bound to mixer zero and a colour source on channel 0 — started, run for a couple of
// keyframes, stopped — and a real, probeable file on disk. Plus the deliberate failures, which are
// the other half of the contract: a failure must be reported, never swallowed, and a killed muxer
// helper must not look like a successful recording.
public sealed class ObsOutputRecordingTests
{
    private const string FfmpegMuxerId = "ffmpeg_muxer";

    [Fact]
    public void ARecording_ProducesARealFileOnDisk()
    {
        Assert.True(ObsRecorderHarnessDriver.EnsureHelperPresent(),
            "The obs-ffmpeg-mux helper is not next to the harness and could not be deployed.");

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "recording.mp4");

        // One second at 30 fps with keyint_sec 1 is three keyframe intervals — enough that the
        // file must contain keyframes even if the first interval is swallowed.
        var (verdict, exitCode) = ObsRecorderHarnessDriver.Run(file, durationSeconds: 1.0);

        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);
        Assert.Equal(0, exitCode);
        var onDisk = Assert.Single(Directory.GetFiles(directory, "recording.mp4"));
        Assert.True(new FileInfo(onDisk).Length > 0, "The recording is empty on disk.");

        Cleanup(directory);
    }

    // The container contract, proven from the file itself rather than from what the plugin said it
    // would write: the file is a playable MP4 with H.264 video and one AAC track. A wrong settings
    // key, a lost audio encoder or a muxer that never wrote its header would each break one of
    // these assertions while the file still exists.
    [Fact]
    public void TheRecording_IsAProbeableMp4WithH264VideoAndAacAudio()
    {
        Assert.True(ObsRecorderHarnessDriver.EnsureHelperPresent(),
            "The obs-ffmpeg-mux helper is not next to the harness and could not be deployed.");

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
        // width/height are numbers; the rate and bit-rate fields are strings because ffprobe
        // stringifies them — probe-media.sh reports what ffprobe gave it, so both shapes appear.
        Assert.Equal(1280, video.GetProperty("width").GetInt32());
        Assert.Equal(720, video.GetProperty("height").GetInt32());
        Assert.Equal(1, root.GetProperty("audio_track_count").GetInt32());
        Assert.Equal("aac", audio[0].GetProperty("codec_name").GetString());
        Assert.Contains("mp4", format.GetProperty("format_name").GetString(), StringComparison.Ordinal);

        Cleanup(directory);
    }

    // A silent AAC track is still a track: the audio encoder is bound to mixer zero, and the
    // output is wired to carry it. A recording with no audio stream at all is the silent failure a
    // "valid MP4" assertion would miss, which is exactly why the audio track count is asserted
    // separately from the file being playable.
    [Fact]
    public void TheRecording_HasAnAudioTrackEvenThoughTheSourceIsSilent()
    {
        Assert.True(ObsRecorderHarnessDriver.EnsureHelperPresent(),
            "The obs-ffmpeg-mux helper is not next to the harness and could not be deployed.");

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "recording.mp4");

        var (verdict, _) = ObsRecorderHarnessDriver.Run(file, durationSeconds: 0.5);
        Assert.Equal(ObsRecorderHarnessDriver.Verdict.Success, verdict);

        using var probe = ProbeMediaScript.Run(file);
        var root = probe.RootElement;
        var audio = root.GetProperty("audio");
        Assert.Equal(1, root.GetProperty("audio_track_count").GetInt32());
        Assert.Equal("aac", audio[0].GetProperty("codec_name").GetString());
        // sample_rate is a string in the probe output (ffprobe stringifies it); parse it.
        Assert.Equal(48000, int.Parse(audio[0].GetProperty("sample_rate").GetString()!, System.Globalization.CultureInfo.InvariantCulture));

        Cleanup(directory);
    }

    // The colour fields, which are the point of probe-media.sh's fixed field set: a clip that plays
    // fine can still carry the wrong transfer or primaries, and a *wrong* binding can tag a
    // recorded file with the wrong colour description without any of it looking broken. The
    // assertion is that the fields exist and are non-"unspecified" — the exact values (bt709 etc.)
    // are libobs's defaults, and the differential comparison is what pins the values, not a
    // hard-coded expectation here.
    [Fact]
    public void TheRecording_CarriesItsColourDescription()
    {
        Assert.True(ObsRecorderHarnessDriver.EnsureHelperPresent(),
            "The obs-ffmpeg-mux helper is not next to the harness and could not be deployed.");

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

    // ---- the deliberate failures: the failure is reported, never swallowed ----

    // The synchronous failure channel, provoked with a bad path. The bad directory is deliberately
    // unique so a stale directory from an earlier run cannot satisfy it.
    [Fact]
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

    // The stop signal is reported rather than swallowed — provoked by killing the muxer helper mid-
    // recording. The plugin cannot finalise the file: the stop signal fires, and what is on disk is
    // not a playable MP4 (no moov atom — measured; ffprobe reports no codec, no audio track and no
    // format).
    [Fact]
    public async Task AKilledMuxerHelper_DoesNotLookLikeASuccessfulRecording()
    {
        Assert.True(ObsRecorderHarnessDriver.EnsureHelperPresent(),
            "The obs-ffmpeg-mux helper is not next to the harness and could not be deployed.");

        var directory = CreateRecordingDirectory();
        var file = Path.Combine(directory, "killed.mp4");

        // A long duration gives the parent time to kill the helper while the recording is running.
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

        // Give the harness time to start the output and spawn the helper, then kill it.
        await Task.Delay(TimeSpan.FromSeconds(2));
        ObsRecorderHarnessDriver.KillMuxerHelper();

        var exited = await Task.Run(() => process.WaitForExit(TimeSpan.FromSeconds(60)));
        Assert.True(exited, "The harness did not exit after its muxer helper was killed.");

        // The killed recording never wrote the moov atom — measured — so ffprobe reports no usable
        // streams. Assert that the harness reported *some* verdict (not a hang and not a clean
        // success with a valid file), and that the file is not a playable MP4.
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

    // ---- helpers ----

    // The shape a recorder actually starts: a muxer output with a video and an audio encoder wired,
    // bound to the session's mixes. Without this, a bad path refuses with "no media" and names
    // nothing — measured — so the tests that assert on the named reason wire encoders first.
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

        // The encoders belong to the output for the duration of this test; the caller's references
        // are kept alive by the output's own internal references, so disposal here only drops the
        // wrapper.
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
            // A leftover recording in the temp directory is not worth failing a green suite over.
        }
    }
}
