// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

// HDR detection is by transfer characteristic: smpte2084 (PQ) or arib-std-b67 (HLG) is HDR,
// anything else is SDR. The parser is the unit under test; the probe's job is to feed it the file's
// actual transfer, and a missing transfer field must read as "unspecified" — the exact absence the
// probe normalises.
public class HdrDetectionTests
{
    [Fact]
    public void Parse_TransferSmpte2084_IsHdr()
    {
        var info = MediaProbe.Parse(StreamJson("smpte2084", "bt2020", "bt2020nc"), "fake.mkv");
        Assert.True(info.IsHdr);
        Assert.Equal("smpte2084", info.ColorTransfer);
    }

    [Fact]
    public void Parse_TransferAribStdB67_IsHdr()
    {
        var info = MediaProbe.Parse(StreamJson("arib-std-b67", "bt2020", "bt2020nc"), "fake.mkv");
        Assert.True(info.IsHdr);
    }

    [Fact]
    public void Parse_TransferBt709_IsSdr()
    {
        var info = MediaProbe.Parse(StreamJson("bt709", "bt709", "bt709"), "fake.mp4");
        Assert.False(info.IsHdr);
    }

    // The load-bearing absence case: a file with no colour metadata at all (the common case for
    // ordinary recordings) must read as SDR "unspecified", not throw.
    [Fact]
    public void Parse_NoColorMetadata_IsSdrUnspecified()
    {
        var info = MediaProbe.Parse(StreamJson(null, null, null), "fake.mp4");
        Assert.False(info.IsHdr);
        Assert.Equal("unspecified", info.ColorTransfer);
        Assert.Equal("unspecified", info.ColorPrimaries);
        Assert.Equal("unspecified", info.ColorSpace);
    }

    [Fact]
    public void Parse_UnknownTransfer_IsSdr()
    {
        var info = MediaProbe.Parse(StreamJson("unknown", "unknown", "unknown"), "fake.mp4");
        Assert.False(info.IsHdr);
    }

    [Fact]
    public void Parse_ReportsAudioStreamCount()
    {
        var json = StreamJson("bt709", "bt709", "bt709", audioStreams: 3);
        var info = MediaProbe.Parse(json, "fake.mp4");
        Assert.Equal(3, info.AudioStreamCount);
    }

    [Fact]
    public void Parse_FrameRateIsRational()
    {
        var info = MediaProbe.Parse(StreamJson("bt709", "bt709", "bt709", frameRate: "60/2"), "fake.mp4");
        Assert.Equal(30.0, info.FrameRate.Value, 10);
        // 60/2 and 30/1 are the same rate.
        Assert.True(Fraction.Same(info.FrameRate, new Fraction(30, 1)));
    }

    private static string StreamJson(string? transfer, string? primaries, string? space,
        int audioStreams = 0, string frameRate = "30/1")
    {
        var videoFields = "\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":320,\"height\":240,"
            + $"\"avg_frame_rate\":\"{frameRate}\",\"r_frame_rate\":\"{frameRate}\",\"pix_fmt\":\"yuv420p\"";
        if (transfer is not null) videoFields += $",\"color_transfer\":\"{transfer}\"";
        if (primaries is not null) videoFields += $",\"color_primaries\":\"{primaries}\"";
        if (space is not null) videoFields += $",\"color_space\":\"{space}\"";

        var streams = $"[{{{videoFields}}}";
        for (var i = 0; i < audioStreams; i++)
            streams += $",{{\"codec_type\":\"audio\",\"index\":{i + 1}}}";
        streams += "]";

        return $"{{\"streams\":{streams},\"format\":{{\"duration\":\"60.0\"}}}}";
    }
}
