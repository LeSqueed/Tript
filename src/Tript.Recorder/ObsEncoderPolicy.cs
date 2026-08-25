// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

internal static class ObsEncoderPolicy
{
    private const string X264Id = "obs_x264";

    // The settings model's placeholder for "the backend decides"; the real software id is obs_x264.
    private const string X264DefaultId = "x264";

    private const string BitrateKey = "bitrate";

    private static readonly string[] HdrCapableCodecs = ["hevc", "av1"];

    private static readonly (int Quality, int Quantiser)[] QualityAnchors =
    [
        (1, 30),
        (3, 28),
        (5, 23),
        (10, 20),
        (18, 16),
        (20, 15)
    ];

    internal const int MinBitrateKbps = 50;
    internal const int MaxBitrateKbps = 100_000;
    internal const int DefaultBitrateKbps = 15_000;

    // An explicit user choice wins while it is still registered; the placeholder does not count.
    internal static string? ResolveVideoEncoderId(string? configuredEncoder)
    {
        if (IsUsableId(configuredEncoder))
            return configuredEncoder;

        var usable = EnumerateUsableEncoderIds();
        return usable.FirstOrDefault(id => !string.Equals(id, X264Id, StringComparison.Ordinal))
               ?? usable.FirstOrDefault();
    }

    // Wrong mode strings can crash obs-ffmpeg, so the mode/key pair is chosen by encoder family.
    internal static (string RateControl, string QualityKey) ResolveRateControlKeys(string encoderId)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        var family = ClassifyFamily(encoderId);
        return (ConstantQualityModeString(family), QuantiserKey(family));
    }

    // Offered UI modes, kept to the families whose key sets are known here.
    internal static IReadOnlyList<RateControlMode> SupportedRateControlModes(string encoderId)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        return ClassifyFamily(encoderId) switch
        {
            EncoderFamily.X264 => [RateControlMode.Crf, RateControlMode.Cbr, RateControlMode.Vbr],
            EncoderFamily.Vaapi => [RateControlMode.Cqp, RateControlMode.Cbr],
            EncoderFamily.Nvenc or EncoderFamily.Amf or EncoderFamily.Qsv =>
                [RateControlMode.Cqp, RateControlMode.Cbr, RateControlMode.Vbr],
            _ => [RateControlMode.Cqp, RateControlMode.Cbr]
        };
    }

    // Settings files travel between machines; the selected encoder has the final say.
    internal static RateControlMode CoerceRateControlMode(string encoderId, RateControlMode requested)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        return SupportedRateControlModes(encoderId).Contains(requested)
            ? requested
            : ConstantQualityMode(ClassifyFamily(encoderId));
    }

    // A null key means the mode does not read that dial on this encoder family.
    internal static EncoderRateControl ResolveRateControl(string encoderId, RateControlMode requested)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        var family = ClassifyFamily(encoderId);
        var mode = CoerceRateControlMode(encoderId, requested);

        return mode switch
        {
            RateControlMode.Crf or RateControlMode.Cqp =>
                new EncoderRateControl(ConstantQualityModeString(family), QuantiserKey(family), null, null),

            // CBR needs no ceiling: max_bitrate is VBR-only and AMF has no ceiling key.
            RateControlMode.Cbr => new EncoderRateControl("CBR", null, BitrateKey, null),

            // x264 VBR is a CRF target with a VBV cap; hardware VBR reads bitrate only.
            RateControlMode.Vbr => new EncoderRateControl(
                "VBR",
                family == EncoderFamily.X264 ? QuantiserKey(family) : null,
                BitrateKey,
                MaxBitrateKey(family)),

            _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "Unknown rate-control mode.")
        };
    }

    // The settings UI hides encoders this machine cannot actually use.
    internal static IReadOnlyList<string> EnumerateUsableEncoderIds()
    {
        var ids = new List<string>();
        if (ObsEncoder.IsTypeRegistered(X264Id))
            ids.Add(X264Id);
        foreach (var id in ObsEncoder.EnumerateTypeIds())
        {
            if (!string.Equals(id, X264Id, StringComparison.Ordinal) && IsH264VideoEncoder(id))
                ids.Add(id);
        }

        return ids;
    }

    // H.264 first preserves the SDR default; HDR planning also admits HEVC/AV1 candidates.
    internal static IReadOnlyList<VideoEncoderCandidate> EnumerateVideoEncoderCandidates()
    {
        var candidates = new List<VideoEncoderCandidate>();

        foreach (var id in EnumerateUsableEncoderIds())
        {
            if (ObsEncoder.GetTypeCodec(id) is { } codec)
                candidates.Add(new VideoEncoderCandidate(id, codec));
        }

        foreach (var id in ObsEncoder.EnumerateTypeIds())
        {
            if (candidates.Any(c => string.Equals(c.Id, id, StringComparison.Ordinal)))
                continue;

            if (ObsEncoder.GetType(id) != ObsEncoderType.Video || ObsEncoder.GetTypeCodec(id) is not { } codec)
                continue;

            if (HdrCapableCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
                candidates.Add(new VideoEncoderCandidate(id, codec));
        }

        return candidates;
    }

    // The app's 1..20 quality scale maps onto H.264's inverted 0..51 quantiser scale.
    internal static int MapQualityToQuantiser(int quality)
    {
        var clamped = Math.Clamp(quality, QualityAnchors[0].Quality, QualityAnchors[^1].Quality);

        for (var i = 1; i < QualityAnchors.Length; i++)
        {
            var (highQuality, highQuantiser) = QualityAnchors[i];
            if (clamped > highQuality)
                continue;

            var (lowQuality, lowQuantiser) = QualityAnchors[i - 1];
            var span = highQuality - lowQuality;
            var position = (double)(clamped - lowQuality) / span;
            var quantiser = lowQuantiser + position * (highQuantiser - lowQuantiser);

            return Math.Clamp((int)Math.Round(quantiser, MidpointRounding.AwayFromZero), MinQuantiser, MaxQuantiser);
        }

        return QualityAnchors[^1].Quantiser;
    }

    internal static int ClampBitrateKbps(int bitrateKbps) =>
        bitrateKbps <= 0 ? DefaultBitrateKbps : Math.Clamp(bitrateKbps, MinBitrateKbps, MaxBitrateKbps);

    internal static int ResolveMaxBitrateKbps(int bitrateKbps, int maxBitrateKbps)
    {
        var target = ClampBitrateKbps(bitrateKbps);
        var ceiling = maxBitrateKbps <= 0 ? (int)(target * 1.5) : maxBitrateKbps;
        return Math.Clamp(ceiling, target, MaxBitrateKbps);
    }

    // x264 must match exactly; hardware families each ship several ids, so those match by substring.
    private static EncoderFamily ClassifyFamily(string encoderId)
    {
        if (string.Equals(encoderId, X264Id, StringComparison.Ordinal))
            return EncoderFamily.X264;

        if (encoderId.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
            return EncoderFamily.Vaapi;

        if (encoderId.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return EncoderFamily.Nvenc;

        if (encoderId.Contains("amf", StringComparison.OrdinalIgnoreCase))
            return EncoderFamily.Amf;

        if (encoderId.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            return EncoderFamily.Qsv;

        return EncoderFamily.Unknown;
    }

    private static RateControlMode ConstantQualityMode(EncoderFamily family) =>
        family == EncoderFamily.X264 ? RateControlMode.Crf : RateControlMode.Cqp;

    private static string ConstantQualityModeString(EncoderFamily family) =>
        family == EncoderFamily.X264 ? "CRF" : "CQP";

    // VAAPI names the quantiser qp; x264 uses crf; NVENC, AMF and QSV use cqp.
    private static string QuantiserKey(EncoderFamily family) => family switch
    {
        EncoderFamily.X264 => "crf",
        EncoderFamily.Vaapi => "qp",
        _ => "cqp"
    };

    // Only NVENC and QSV document a max_bitrate key.
    private static string? MaxBitrateKey(EncoderFamily family) => family switch
    {
        EncoderFamily.Nvenc or EncoderFamily.Qsv => "max_bitrate",
        _ => null
    };

    internal static bool IsUsableId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        !string.Equals(id, X264DefaultId, StringComparison.OrdinalIgnoreCase) &&
        IsH264VideoEncoder(id);

    private static bool IsH264VideoEncoder(string id) =>
        ObsEncoder.IsTypeRegistered(id) &&
        ObsEncoder.GetTypeCodec(id) is { } codec &&
        codec.Equals("h264", StringComparison.OrdinalIgnoreCase) &&
        ObsEncoder.GetType(id) == ObsEncoderType.Video;

    // H.264's quantiser range; the clamp keeps future presets inside plugin bounds.
    private const int MinQuantiser = 0;
    private const int MaxQuantiser = 51;

    // Unknown is valid: third-party H.264 encoders still get conservative settings.
    private enum EncoderFamily
    {
        X264,
        Vaapi,
        Nvenc,
        Amf,
        Qsv,
        Unknown
    }
}

// A null key means the mode does not read that dial on this family.
internal readonly record struct EncoderRateControl(
    string Mode,
    string? QuantiserKey,
    string? BitrateKey,
    string? MaxBitrateKey);
