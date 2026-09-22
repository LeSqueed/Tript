// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public sealed record VideoEncoder(
    string Name,
    bool IsHardware,
    IReadOnlyList<string> OutputArgs,
    IReadOnlyList<string> InputArgs,
    IReadOnlyList<string> GlobalArgs,
    string FilterSuffix)
{
    private static readonly IReadOnlyList<string> HardwareDecode = ["-hwaccel", "auto"];

    public static VideoEncoder Software { get; } = new("libx264", false, [], [], [], string.Empty);

    public static VideoEncoder Nvenc { get; } = new("h264_nvenc", true,
        ["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "23", "-b:v", "0", "-pix_fmt", "yuv420p"],
        HardwareDecode, [], string.Empty);

    public static VideoEncoder Amf { get; } = new("h264_amf", true,
        ["-quality", "quality", "-rc", "cqp", "-qp_i", "21", "-qp_p", "23", "-qp_b", "25", "-pix_fmt", "yuv420p"],
        HardwareDecode, [], string.Empty);

    public static VideoEncoder Qsv { get; } = new("h264_qsv", true,
        ["-preset", "medium", "-global_quality", "23", "-pix_fmt", "nv12"],
        HardwareDecode, [], string.Empty);

    public static VideoEncoder Vaapi { get; } = new("h264_vaapi", true,
        ["-rc_mode", "CQP", "-qp", "23"],
        HardwareDecode, ["-vaapi_device", "/dev/dri/renderD128"], ",format=nv12,hwupload");

    public static IReadOnlyList<VideoEncoder> PlatformHardware() =>
        OperatingSystem.IsWindows() ? [Nvenc, Amf, Qsv] : [Nvenc, Vaapi, Qsv];
}
