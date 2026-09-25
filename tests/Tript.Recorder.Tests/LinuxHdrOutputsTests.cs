// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Recorder.Tests;

public sealed class LinuxHdrOutputsTests
{
    private const string SwayOutputs = """
        [
          { "name": "DP-2", "active": true, "hdr_enabled": true,
            "current_mode": { "width": 2560, "height": 1440, "refresh": 164999 } },
          { "name": "HDMI-A-1", "active": true, "hdr_enabled": false,
            "current_mode": { "width": 1920, "height": 1080, "refresh": 60000 } },
          { "name": "DP-3", "active": false, "hdr_enabled": true,
            "current_mode": { "width": 3840, "height": 2160, "refresh": 60000 } }
        ]
        """;

    [Fact]
    public void SwayOutputs_AreReadWithTheirModeSizeAndHdrState() =>
        Assert.Equal(
            [new DisplayOutput("DP-2", 2560, 1440, true), new DisplayOutput("HDMI-A-1", 1920, 1080, false)],
            LinuxHdrOutputs.ParseSway(SwayOutputs));

    [Fact]
    public void ASwayWithoutHdrSupport_ReportsItsOutputsAsSdr() =>
        Assert.False(Assert.Single(LinuxHdrOutputs.ParseSway(
            """[{ "name": "DP-1", "active": true, "current_mode": { "width": 1920, "height": 1080 } }]""")).Hdr);

    [Fact]
    public void KScreenOutputs_AreReadFromTheirCurrentMode() =>
        Assert.Equal(
            [new DisplayOutput("DP-1", 3840, 2160, true)],
            LinuxHdrOutputs.ParseKScreen("""
                { "outputs": [
                  { "name": "DP-1", "enabled": true, "hdr": true, "currentModeId": "7",
                    "modes": [ { "id": "3", "size": { "width": 1920, "height": 1080 } },
                               { "id": "7", "size": { "width": 3840, "height": 2160 } } ] },
                  { "name": "HDMI-A-1", "enabled": false, "hdr": false, "currentModeId": "1",
                    "modes": [ { "id": "1", "size": { "width": 1920, "height": 1080 } } ] }
                ] }
                """));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void UnreadableOutputLists_AreEmpty(string json)
    {
        Assert.Empty(LinuxHdrOutputs.ParseSway(json));
        Assert.Empty(LinuxHdrOutputs.ParseKScreen(json));
    }

    [Fact]
    public void ACaptureTheSizeOfAnHdrOutput_IsPq() =>
        Assert.True(LinuxHdrOutputs.CaptureIsPq(LinuxHdrOutputs.ParseSway(SwayOutputs), 2560, 1440));

    [Fact]
    public void ACaptureTheSizeOfAnSdrOutput_IsNotPq() =>
        Assert.False(LinuxHdrOutputs.CaptureIsPq(LinuxHdrOutputs.ParseSway(SwayOutputs), 1920, 1080));

    [Fact]
    public void OutputsOfTheSameSizeThatDisagree_LeaveTheAnswerUnknown() =>
        Assert.Null(LinuxHdrOutputs.CaptureIsPq(
            [new DisplayOutput("DP-1", 2560, 1440, true), new DisplayOutput("DP-2", 2560, 1440, false)], 2560, 1440));

    [Fact]
    public void ACaptureMatchingNoOutput_IsPqOnlyWhenEveryOutputIsHdr()
    {
        Assert.True(LinuxHdrOutputs.CaptureIsPq(
            [new DisplayOutput("DP-1", 2560, 1440, true), new DisplayOutput("DP-2", 1920, 1080, true)], 800, 600));
        Assert.Null(LinuxHdrOutputs.CaptureIsPq(LinuxHdrOutputs.ParseSway(SwayOutputs), 800, 600));
    }

    [Fact]
    public void NoOutputInformation_LeavesTheAnswerUnknown() =>
        Assert.Null(LinuxHdrOutputs.CaptureIsPq([], 2560, 1440));
}
