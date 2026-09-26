// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;

namespace Tript.Recorder;

public sealed record VideoEncoderCandidate(string Id, string Codec);

public sealed record HdrPlan
{
    public required bool UseHdr { get; init; }
    public required string EncoderId { get; init; }

    public ObsVideoFormat OutputFormat => UseHdr ? ObsVideoFormat.P010 : ObsVideoFormat.Nv12;
    public ObsColorSpace ColorSpace => UseHdr ? ObsColorSpace.Rec2100Pq : ObsColorSpace.Rec709;

    public string? Profile { get; init; }

    public bool ForceSdrOnCapture => !UseHdr;

    public required string Reason { get; init; }
}

public static class HdrPlanner
{
    private static readonly string[] HdrCapableCodecs = ["hevc", "av1"];

    private const string Hevc = "hevc";

    public static bool IsHdrCapable(VideoEncoderCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return HdrCapableCodecs.Contains(candidate.Codec, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsHdr(ObsSourceColorSpace space) =>
        space is ObsSourceColorSpace.Extended709 or ObsSourceColorSpace.Scrgb709;

    public static HdrPlan Decide(
        ObsSourceColorSpace? capturedColorSpace,
        bool displayIsHdr,
        bool hdrEnabledInSettings,
        IReadOnlyList<VideoEncoderCandidate> registered,
        string? configuredEncoderId)
    {
        ArgumentNullException.ThrowIfNull(registered);

        if (registered.Count == 0)
            throw new ArgumentException("No video encoder is registered.", nameof(registered));

        var sdrEncoder = ResolveSdrEncoder(registered, configuredEncoderId);

        var sourceIsHdr = capturedColorSpace is { } space && IsHdr(space);
        var contentIsHdr = sourceIsHdr || displayIsHdr;

        if (!contentIsHdr)
        {
            var why = capturedColorSpace is { } sdrSpace
                ? $"the captured source reports {sdrSpace} and no display is in HDR mode"
                : "nothing is hooked and no display is in HDR mode";
            return Sdr(sdrEncoder, why);
        }

        if (!hdrEnabledInSettings)
            return Sdr(sdrEncoder, "HDR recording is turned off in settings");

        var hdrEncoder = ResolveHdrEncoder(registered, configuredEncoderId);
        if (hdrEncoder is null)
            return Sdr(sdrEncoder, "no registered encoder can encode HDR");

        var because = sourceIsHdr
            ? $"the captured source reports {capturedColorSpace}"
            : "a display is in HDR mode";

        return new HdrPlan
        {
            UseHdr = true,
            EncoderId = hdrEncoder.Id,
            Profile = hdrEncoder.Codec.Equals(Hevc, StringComparison.OrdinalIgnoreCase)
                      && ObsEncoderPolicy.TakesNamedProfile(hdrEncoder.Id) ? "main10" : null,
            Reason = $"{because} and '{hdrEncoder.Id}' can encode it"
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

        return registered.OrderBy(HardwarePreference).First().Id;
    }

    private static VideoEncoderCandidate? ResolveHdrEncoder(
        IReadOnlyList<VideoEncoderCandidate> registered, string? configuredEncoderId)
    {
        if (configuredEncoderId is not null &&
            registered.FirstOrDefault(c => c.Id.Equals(configuredEncoderId, StringComparison.Ordinal)) is { } chosen &&
            IsHdrCapable(chosen))
        {
            return chosen;
        }

        return registered
            .Where(IsHdrCapable)
            .OrderBy(HardwarePreference)
            .FirstOrDefault();
    }

    internal static int HardwarePreference(VideoEncoderCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (SoftwareEncoderIds.Contains(candidate.Id, StringComparer.OrdinalIgnoreCase))
            return 2;

        return candidate.Id.Contains("texture", StringComparison.OrdinalIgnoreCase) ||
               candidate.Id.EndsWith("_tex", StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;
    }

    private static readonly string[] SoftwareEncoderIds = ["obs_x264", "ffmpeg_svt_av1", "ffmpeg_aom_av1"];
}
