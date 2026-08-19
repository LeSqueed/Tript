// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;

namespace Tript.Recorder;

// A registered video encoder, reduced to the two facts the HDR decision turns on.
public sealed record VideoEncoderCandidate(string Id, string Codec);

// What the mix and the encoder should be for one recording. Decided once, before the output is
// created: obs_reset_video answers CurrentlyActive (-4) once an output is running, so the canvas
// colour space cannot be revised mid-recording.
public sealed record HdrPlan
{
    public required bool UseHdr { get; init; }
    public required string EncoderId { get; init; }

    public ObsVideoFormat OutputFormat => UseHdr ? ObsVideoFormat.P010 : ObsVideoFormat.Nv12;
    public ObsColorSpace ColorSpace => UseHdr ? ObsColorSpace.Rec2100Pq : ObsColorSpace.Rec709;

    // The h264/hevc profile key the encoder families read. HEVC needs main10 to carry ten bits at
    // all; an HDR mix into "main" is silently truncated to eight.
    public string? Profile { get; init; }

    // Told to the capture sources when the mix is SDR. An HDR game presents an FP16 scRGB swapchain
    // and win-capture hands that texture over as-is; composited into a Rec.709 canvas with nothing
    // saying otherwise, the result is not merely dark but unusable. force_sdr makes the plugin
    // tonemap on the way in, which is the correct answer whenever we are not recording HDR.
    public bool ForceSdrOnCapture => !UseHdr;

    // Why this plan and not another, for the one log line that explains a recording's colour to a
    // user looking at an unexpectedly flat file.
    public required string Reason { get; init; }
}

public static class HdrPlanner
{
    // The codecs whose OBS encoders can carry Rec.2100 PQ at ten bits. H.264 is absent deliberately:
    // its Hi10 profile exists but no OBS H.264 encoder exposes an HDR path, so an "HDR" recording on
    // one would be a PQ-tagged eight-bit file.
    private static readonly string[] HdrCapableCodecs = ["hevc", "av1"];

    // Preferred last-to-first, so the search below reads in preference order.
    private const string Hevc = "hevc";

    public static bool IsHdrCapable(VideoEncoderCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return HdrCapableCodecs.Contains(candidate.Codec, StringComparer.OrdinalIgnoreCase);
    }

    // The mix and encoder for one recording.
    //
    // HDR is taken only when all three hold: the display we capture is in HDR mode, the user has not
    // turned it off, and some registered encoder can actually encode it. Any one missing falls back
    // to SDR with force_sdr on the capture sources — which is what makes an HDR game record at all,
    // rather than as a black or washed-out frame.
    //
    // A configured encoder is honoured in SDR. It is NOT honoured in HDR when it cannot encode HDR:
    // the alternative is writing a PQ-tagged file the encoder truncated to eight bits, which reads
    // as a corrupt recording rather than as a setting that did not apply.
    public static HdrPlan Decide(
        bool displayIsHdr,
        bool hdrEnabledInSettings,
        IReadOnlyList<VideoEncoderCandidate> registered,
        string? configuredEncoderId)
    {
        ArgumentNullException.ThrowIfNull(registered);

        if (registered.Count == 0)
            throw new ArgumentException("No video encoder is registered.", nameof(registered));

        var sdrEncoder = ResolveSdrEncoder(registered, configuredEncoderId);

        if (!displayIsHdr)
            return Sdr(sdrEncoder, "the captured display is not in HDR mode");

        if (!hdrEnabledInSettings)
            return Sdr(sdrEncoder, "HDR recording is turned off in settings");

        var hdrEncoder = ResolveHdrEncoder(registered, configuredEncoderId);
        if (hdrEncoder is null)
            return Sdr(sdrEncoder, "no registered encoder can encode HDR");

        return new HdrPlan
        {
            UseHdr = true,
            EncoderId = hdrEncoder.Id,
            Profile = hdrEncoder.Codec.Equals(Hevc, StringComparison.OrdinalIgnoreCase) ? "main10" : null,
            Reason = $"the captured display is in HDR mode and '{hdrEncoder.Id}' can encode it"
        };
    }

    private static HdrPlan Sdr(string encoderId, string reason) => new()
    {
        UseHdr = false,
        EncoderId = encoderId,
        Profile = null,
        Reason = reason
    };

    private static string ResolveSdrEncoder(
        IReadOnlyList<VideoEncoderCandidate> registered, string? configuredEncoderId)
    {
        if (configuredEncoderId is not null &&
            registered.FirstOrDefault(c => c.Id.Equals(configuredEncoderId, StringComparison.Ordinal)) is { } chosen)
        {
            return chosen.Id;
        }

        return registered[0].Id;
    }

    // The configured encoder when it can do the job, so a user who picked one keeps it; otherwise the
    // first registered encoder that can, in the order the caller enumerated them.
    private static VideoEncoderCandidate? ResolveHdrEncoder(
        IReadOnlyList<VideoEncoderCandidate> registered, string? configuredEncoderId)
    {
        if (configuredEncoderId is not null &&
            registered.FirstOrDefault(c => c.Id.Equals(configuredEncoderId, StringComparison.Ordinal)) is { } chosen &&
            IsHdrCapable(chosen))
        {
            return chosen;
        }

        return registered.FirstOrDefault(IsHdrCapable);
    }
}
