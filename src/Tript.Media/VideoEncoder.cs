// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public sealed record VideoEncoder(
    string Name,
    bool IsHardware,
    IReadOnlyList<string> OutputArgs,
    IReadOnlyList<string> InputArgs,
    IReadOnlyList<string> GlobalArgs,
    string FilterSuffix,
    string? Device = null)
{
    private static readonly IReadOnlyList<string> HardwareDecode = ["-hwaccel", "auto"];

    public string Key => Device is null ? Name : $"{Name} on {Device}";

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

    public static VideoEncoder VaapiOn(string renderNode) => new("h264_vaapi", true,
        ["-rc_mode", "CQP", "-qp", "23"],
        HardwareDecode, ["-vaapi_device", renderNode], ",format=nv12,hwupload", renderNode);

    public static IReadOnlyList<VideoEncoder> PlatformHardware() =>
        OperatingSystem.IsWindows()
            ? PlatformHardware(windows: true, [])
            : PlatformHardware(windows: false, VaapiRenderNodes.Discover());

    internal static IReadOnlyList<VideoEncoder> PlatformHardware(bool windows, IReadOnlyList<string> vaapiRenderNodes) =>
        windows ? [Nvenc, Amf, Qsv] : [Nvenc, .. vaapiRenderNodes.Select(VaapiOn), Qsv];
}
