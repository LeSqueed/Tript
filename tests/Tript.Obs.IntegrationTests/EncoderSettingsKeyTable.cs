// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs.IntegrationTests;

// The encoder settings keys the specification names, transcribed so the binding can be driven from
// them rather than from whatever a test author remembered.
//
// None of these keys is in libobs. They are plugin-private string literals, which is precisely why
// they need a table: a mistyped key is not an error, it is a settings object that quietly carries a
// key nothing reads, so the encoder runs on its default and produces a plausible file at the wrong
// bitrate, the wrong quality or the wrong keyframe interval.
//
// The families are addressed by encoder id, not by runtime version. From OBS 31 both NVENC key sets
// are live at the same time serving different ids, so "which version is this" is the wrong question
// and "which id did we create" is the right one.
//
// Public, unlike the rest of the test support here, only because xunit refuses to source theory data
// from a member it cannot see.
public sealed record EncoderSettingKey
{
    public required string Family { get; init; }

    public required string Key { get; init; }

    public required ObsSettingsValueType Type { get; init; }

    // Deliberately not the specified default, so a round-trip cannot pass by coincidence on an
    // object that happens to be pre-populated.
    public required object Sample { get; init; }

    // The value the plugin falls back on, where the specification records one.
    public object? SpecifiedDefault { get; init; }

    // For keys the plugin validates against a fixed list. Every one of these has to survive
    // marshalling byte for byte: several are compared case-sensitively on the plugin side.
    public IReadOnlyList<string> AcceptedValues { get; init; } = [];
}

public static class EncoderSettingsKeyTable
{
    // Ids, not versions. Named here so a family label is traceable to the thing that reads the keys.
    public const string NvencTexture = "NVENC texture (obs_nvenc_h264_tex, obs_nvenc_hevc_tex, obs_nvenc_av1_tex)";
    public const string NvencLegacy = "NVENC legacy (jim_nvenc, ffmpeg_nvenc)";
    public const string Amf = "AMD AMF (h264_texture_amf, h265_texture_amf, av1_texture_amf)";
    public const string Qsv = "Intel QSV (obs_qsv11_v2, obs_qsv11_hevc, obs_qsv11_av1)";
    public const string X264 = "x264 (obs_x264)";

    public static IReadOnlyList<string> Families { get; } = [NvencTexture, NvencLegacy, Amf, Qsv, X264];

    public static IReadOnlyList<EncoderSettingKey> All { get; } =
    [
        // ---- NVENC, modern texture encoders ----
        // The default rate control is written lowercase "cbr", while "CQP" and "lossless" are the
        // two values the plugin compares case-sensitively. Both facts are only expressible if the
        // binding moves these strings without touching their case.
        new()
        {
            Family = NvencTexture, Key = "rate_control", Type = ObsSettingsValueType.String,
            Sample = "CQVBR", SpecifiedDefault = "cbr",
            AcceptedValues = ["CBR", "cbr", "CQP", "VBR", "CQVBR", "lossless"]
        },
        new() { Family = NvencTexture, Key = "bitrate", Type = ObsSettingsValueType.Number, Sample = 45000L, SpecifiedDefault = 10000L },
        new() { Family = NvencTexture, Key = "max_bitrate", Type = ObsSettingsValueType.Number, Sample = 22500L, SpecifiedDefault = 10000L },
        new() { Family = NvencTexture, Key = "cqp", Type = ObsSettingsValueType.Number, Sample = 63L, SpecifiedDefault = 20L },
        new() { Family = NvencTexture, Key = "target_quality", Type = ObsSettingsValueType.Number, Sample = 31L, SpecifiedDefault = 20L },
        new() { Family = NvencTexture, Key = "keyint_sec", Type = ObsSettingsValueType.Number, Sample = 7L },
        new()
        {
            Family = NvencTexture, Key = "preset", Type = ObsSettingsValueType.String,
            Sample = "p7", SpecifiedDefault = "p5",
            AcceptedValues = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"]
        },
        new()
        {
            Family = NvencTexture, Key = "tune", Type = ObsSettingsValueType.String,
            Sample = "uhq", SpecifiedDefault = "hq",
            AcceptedValues = ["uhq", "hq", "ll", "ull"]
        },
        new()
        {
            Family = NvencTexture, Key = "multipass", Type = ObsSettingsValueType.String,
            Sample = "fullres", SpecifiedDefault = "qres",
            AcceptedValues = ["disabled", "qres", "fullres"]
        },
        new()
        {
            Family = NvencTexture, Key = "profile", Type = ObsSettingsValueType.String,
            Sample = "high10", SpecifiedDefault = "high",
            AcceptedValues = ["high10", "high", "main", "baseline", "main10"]
        },
        new() { Family = NvencTexture, Key = "bf", Type = ObsSettingsValueType.Number, Sample = 4L, SpecifiedDefault = 2L },
        new() { Family = NvencTexture, Key = "bframe_ref_mode", Type = ObsSettingsValueType.Number, Sample = 2L },
        new() { Family = NvencTexture, Key = "lookahead", Type = ObsSettingsValueType.Boolean, Sample = true },
        new() { Family = NvencTexture, Key = "adaptive_quantization", Type = ObsSettingsValueType.Boolean, Sample = false, SpecifiedDefault = true },
        new() { Family = NvencTexture, Key = "device", Type = ObsSettingsValueType.Number, Sample = -1L, SpecifiedDefault = -1L },
        new() { Family = NvencTexture, Key = "split_encode", Type = ObsSettingsValueType.Number, Sample = 4L },
        new() { Family = NvencTexture, Key = "opts", Type = ObsSettingsValueType.String, Sample = "rcParams.lowDelayKeyFrameScale=1 encodeConfig.gopLength=120" },
        new() { Family = NvencTexture, Key = "repeat_headers", Type = ObsSettingsValueType.Boolean, Sample = true },
        new() { Family = NvencTexture, Key = "force_cuda_tex", Type = ObsSettingsValueType.Boolean, Sample = true },
        new() { Family = NvencTexture, Key = "disable_scenecut", Type = ObsSettingsValueType.Boolean, Sample = true },

        // ---- NVENC, legacy reroute stubs ----
        // preset2 rather than preset, psycho_aq rather than adaptive_quantization, gpu rather than
        // device. Writing preset2 to a modern id is silently ignored, which is the whole reason the
        // two families are separate rows here rather than one merged set.
        new()
        {
            Family = NvencLegacy, Key = "rate_control", Type = ObsSettingsValueType.String,
            Sample = "lossless",
            AcceptedValues = ["CBR", "CQP", "VBR", "lossless"]
        },
        new() { Family = NvencLegacy, Key = "bitrate", Type = ObsSettingsValueType.Number, Sample = 8000L, SpecifiedDefault = 2500L },
        new() { Family = NvencLegacy, Key = "max_bitrate", Type = ObsSettingsValueType.Number, Sample = 9000L, SpecifiedDefault = 5000L },
        new() { Family = NvencLegacy, Key = "cqp", Type = ObsSettingsValueType.Number, Sample = 18L },
        new() { Family = NvencLegacy, Key = "keyint_sec", Type = ObsSettingsValueType.Number, Sample = 3L },
        new()
        {
            Family = NvencLegacy, Key = "preset2", Type = ObsSettingsValueType.String,
            Sample = "p6",
            AcceptedValues = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"]
        },
        new() { Family = NvencLegacy, Key = "tune", Type = ObsSettingsValueType.String, Sample = "ll", AcceptedValues = ["hq", "ll", "ull"] },
        new() { Family = NvencLegacy, Key = "multipass", Type = ObsSettingsValueType.String, Sample = "qres", AcceptedValues = ["disabled", "qres", "fullres"] },
        new() { Family = NvencLegacy, Key = "profile", Type = ObsSettingsValueType.String, Sample = "main", AcceptedValues = ["high", "main", "baseline"] },
        new() { Family = NvencLegacy, Key = "lookahead", Type = ObsSettingsValueType.Boolean, Sample = true },
        new() { Family = NvencLegacy, Key = "psycho_aq", Type = ObsSettingsValueType.Boolean, Sample = true },
        new() { Family = NvencLegacy, Key = "gpu", Type = ObsSettingsValueType.Number, Sample = 8L },
        new() { Family = NvencLegacy, Key = "bf", Type = ObsSettingsValueType.Number, Sample = 2L },
        new() { Family = NvencLegacy, Key = "repeat_headers", Type = ObsSettingsValueType.Boolean, Sample = true },
        new() { Family = NvencLegacy, Key = "disable_scenecut", Type = ObsSettingsValueType.Boolean, Sample = true },

        // ---- AMD AMF ----
        // No max_bitrate, no tune, no look-ahead and no adapter index. The QVBR quality level rides
        // on cqp, and "highQuality" is the one camel-cased value in any of these tables.
        new()
        {
            Family = Amf, Key = "rate_control", Type = ObsSettingsValueType.String,
            Sample = "VBR_LAT", SpecifiedDefault = "CBR",
            AcceptedValues = ["CBR", "CQP", "VBR", "VBR_LAT", "QVBR", "HQVBR", "HQCBR"]
        },
        new() { Family = Amf, Key = "bitrate", Type = ObsSettingsValueType.Number, Sample = 100000L, SpecifiedDefault = 6000L },
        new() { Family = Amf, Key = "cqp", Type = ObsSettingsValueType.Number, Sample = 63L, SpecifiedDefault = 20L },
        new() { Family = Amf, Key = "keyint_sec", Type = ObsSettingsValueType.Number, Sample = 10L },
        new()
        {
            Family = Amf, Key = "preset", Type = ObsSettingsValueType.String,
            Sample = "highQuality", SpecifiedDefault = "quality",
            AcceptedValues = ["highQuality", "quality", "balanced", "speed"]
        },
        new()
        {
            Family = Amf, Key = "profile", Type = ObsSettingsValueType.String,
            Sample = "constrained_baseline", SpecifiedDefault = "high",
            AcceptedValues = ["high", "main", "baseline", "constrained_baseline"]
        },
        new() { Family = Amf, Key = "bf", Type = ObsSettingsValueType.Number, Sample = 5L, SpecifiedDefault = 2L },
        new() { Family = Amf, Key = "pre_analysis", Type = ObsSettingsValueType.Boolean, Sample = true },
        new() { Family = Amf, Key = "screen_content_tools", Type = ObsSettingsValueType.Boolean, Sample = true },
        new() { Family = Amf, Key = "palette_mode", Type = ObsSettingsValueType.Boolean, Sample = true },
        new() { Family = Amf, Key = "ffmpeg_opts", Type = ObsSettingsValueType.String, Sample = "MaxNumRefFrames=4 HighMotionQualityBoostEnable=1" },
        new() { Family = Amf, Key = "repeat_headers", Type = ObsSettingsValueType.Boolean, Sample = true },

        // ---- Intel QSV ----
        // The B-frame key is bframes here and bf everywhere else, target_usage carries the
        // speed/quality dial with no preset key at all, and __ver is a marker OBS stamps into the
        // settings object itself.
        new()
        {
            Family = Qsv, Key = "rate_control", Type = ObsSettingsValueType.String,
            Sample = "ICQ", SpecifiedDefault = "CBR",
            AcceptedValues = ["CBR", "VBR", "CQP", "ICQ", "LA_CBR", "LA_VBR", "LA_ICQ", "VCM", "AVBR"]
        },
        new() { Family = Qsv, Key = "bitrate", Type = ObsSettingsValueType.Number, Sample = 10000000L, SpecifiedDefault = 5000L },
        new() { Family = Qsv, Key = "max_bitrate", Type = ObsSettingsValueType.Number, Sample = 9999999L, SpecifiedDefault = 6000L },
        new() { Family = Qsv, Key = "cqp", Type = ObsSettingsValueType.Number, Sample = 63L, SpecifiedDefault = 23L },
        new() { Family = Qsv, Key = "qpi", Type = ObsSettingsValueType.Number, Sample = 11L, SpecifiedDefault = 23L },
        new() { Family = Qsv, Key = "qpp", Type = ObsSettingsValueType.Number, Sample = 12L, SpecifiedDefault = 23L },
        new() { Family = Qsv, Key = "qpb", Type = ObsSettingsValueType.Number, Sample = 13L, SpecifiedDefault = 23L },
        new() { Family = Qsv, Key = "icq_quality", Type = ObsSettingsValueType.Number, Sample = 51L, SpecifiedDefault = 23L },
        new()
        {
            Family = Qsv, Key = "target_usage", Type = ObsSettingsValueType.String,
            Sample = "TU1", SpecifiedDefault = "TU4",
            AcceptedValues =
            [
                "TU1", "TU2", "TU3", "TU4", "TU5", "TU6", "TU7",
                "veryslow", "quality", "slower", "slow", "medium", "balanced", "fast", "faster", "veryfast", "speed"
            ]
        },
        new()
        {
            Family = Qsv, Key = "profile", Type = ObsSettingsValueType.String,
            Sample = "main10", SpecifiedDefault = "high",
            AcceptedValues = ["high", "main", "baseline", "main10"]
        },
        new() { Family = Qsv, Key = "keyint_sec", Type = ObsSettingsValueType.Number, Sample = 20L },
        new()
        {
            Family = Qsv, Key = "latency", Type = ObsSettingsValueType.String,
            Sample = "ultra-low", SpecifiedDefault = "normal",
            AcceptedValues = ["ultra-low", "low", "normal"]
        },
        new() { Family = Qsv, Key = "bframes", Type = ObsSettingsValueType.Number, Sample = 0L, SpecifiedDefault = 3L },
        new() { Family = Qsv, Key = "bf", Type = ObsSettingsValueType.Number, Sample = 2L },
        new() { Family = Qsv, Key = "__ver", Type = ObsSettingsValueType.Number, Sample = 2L },
        // Read once and then erased by the plugin, which rewrites them into latency. Present so the
        // table is a complete account of what the plugin reads; nothing should write them.
        new() { Family = Qsv, Key = "async_depth", Type = ObsSettingsValueType.Number, Sample = 4L },
        new() { Family = Qsv, Key = "la_depth", Type = ObsSettingsValueType.Number, Sample = 40L },
        new() { Family = Qsv, Key = "repeat_headers", Type = ObsSettingsValueType.Boolean, Sample = true },

        // ---- x264 ----
        // No lossless rate control and no vbv key: VBV is bitrate plus use_bufsize plus buffer_size.
        // profile and tune both default to the empty string, which is a value rather than an absence.
        new()
        {
            Family = X264, Key = "rate_control", Type = ObsSettingsValueType.String,
            Sample = "CRF", SpecifiedDefault = "CBR",
            AcceptedValues = ["CBR", "ABR", "VBR", "CRF"]
        },
        new() { Family = X264, Key = "bitrate", Type = ObsSettingsValueType.Number, Sample = 10000000L, SpecifiedDefault = 6000L },
        new() { Family = X264, Key = "use_bufsize", Type = ObsSettingsValueType.Boolean, Sample = true, SpecifiedDefault = false },
        new() { Family = X264, Key = "buffer_size", Type = ObsSettingsValueType.Number, Sample = 10000000L, SpecifiedDefault = 6000L },
        new() { Family = X264, Key = "crf", Type = ObsSettingsValueType.Number, Sample = 0L, SpecifiedDefault = 23L },
        new() { Family = X264, Key = "keyint_sec", Type = ObsSettingsValueType.Number, Sample = 20L },
        new()
        {
            Family = X264, Key = "preset", Type = ObsSettingsValueType.String,
            Sample = "placebo", SpecifiedDefault = "veryfast",
            AcceptedValues =
            [
                "ultrafast", "superfast", "veryfast", "faster", "fast",
                "medium", "slow", "slower", "veryslow", "placebo"
            ]
        },
        new()
        {
            Family = X264, Key = "profile", Type = ObsSettingsValueType.String,
            Sample = "high", SpecifiedDefault = "",
            AcceptedValues = ["", "baseline", "main", "high"]
        },
        new()
        {
            Family = X264, Key = "tune", Type = ObsSettingsValueType.String,
            Sample = "zerolatency", SpecifiedDefault = "",
            AcceptedValues = ["", "film", "animation", "grain", "stillimage", "psnr", "ssim", "fastdecode", "zerolatency"]
        },
        new() { Family = X264, Key = "x264opts", Type = ObsSettingsValueType.String, Sample = "vbv-maxrate=6000 vbv-bufsize=6000" },
        new() { Family = X264, Key = "bf", Type = ObsSettingsValueType.Number, Sample = 3L },
        new() { Family = X264, Key = "vfr", Type = ObsSettingsValueType.Boolean, Sample = true, SpecifiedDefault = false },
        new() { Family = X264, Key = "cbr", Type = ObsSettingsValueType.Boolean, Sample = true },
        new() { Family = X264, Key = "repeat_headers", Type = ObsSettingsValueType.Boolean, Sample = true }
    ];

    public static IEnumerable<object[]> FamilyNames() => Families.Select(family => new object[] { family });

    public static IReadOnlyList<EncoderSettingKey> For(string family) =>
        All.Where(key => key.Family == family).ToArray();

    // Writes the sample with the type the specification assigns the key.
    public static void Write(ObsSettings settings, EncoderSettingKey key, object value)
    {
        switch (key.Type)
        {
            case ObsSettingsValueType.String:
                settings.SetString(key.Key, (string)value);
                break;
            case ObsSettingsValueType.Number:
                settings.SetInt(key.Key, (long)value);
                break;
            case ObsSettingsValueType.Boolean:
                settings.SetBool(key.Key, (bool)value);
                break;
            default:
                throw new NotSupportedException($"No encoder settings key is of type {key.Type}.");
        }
    }

    public static void WriteDefault(ObsSettings settings, EncoderSettingKey key, object value)
    {
        switch (key.Type)
        {
            case ObsSettingsValueType.String:
                settings.SetDefaultString(key.Key, (string)value);
                break;
            case ObsSettingsValueType.Number:
                settings.SetDefaultInt(key.Key, (long)value);
                break;
            case ObsSettingsValueType.Boolean:
                settings.SetDefaultBool(key.Key, (bool)value);
                break;
            default:
                throw new NotSupportedException($"No encoder settings key is of type {key.Type}.");
        }
    }

    public static object Read(ObsSettings settings, EncoderSettingKey key) => key.Type switch
    {
        ObsSettingsValueType.String => settings.GetString(key.Key),
        ObsSettingsValueType.Number => settings.GetInt(key.Key),
        ObsSettingsValueType.Boolean => settings.GetBool(key.Key),
        _ => throw new NotSupportedException($"No encoder settings key is of type {key.Type}.")
    };

    public static object ReadDefault(ObsSettings settings, EncoderSettingKey key) => key.Type switch
    {
        ObsSettingsValueType.String => settings.GetDefaultString(key.Key),
        ObsSettingsValueType.Number => settings.GetDefaultInt(key.Key),
        ObsSettingsValueType.Boolean => settings.GetDefaultBool(key.Key),
        _ => throw new NotSupportedException($"No encoder settings key is of type {key.Type}.")
    };
}
