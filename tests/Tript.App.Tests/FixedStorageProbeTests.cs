// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Xunit;

namespace Tript.App.Tests;

public sealed class FixedStorageProbeTests
{
    private static Func<string, string?> Environment(string? freeBytes) =>
        name => name == FixedStorageProbe.FreeBytesEnvironmentVariable ? freeBytes : null;

    [Fact]
    public void ForFakeRecorder_ReportsTheConfiguredFreeSpaceForEveryPath()
    {
        var probe = FixedStorageProbe.ForFakeRecorder(fakeRecorder: true, Environment("5000"));

        Assert.NotNull(probe);
        Assert.Equal(5000, probe.Measure("/some/content/root")?.FreeBytes);
        Assert.Equal(5000, probe.Measure("/some/content/root/.scratch")?.FreeBytes);
    }

    [Fact]
    public void ForFakeRecorder_PutsContentAndScratchOnOneVolume()
    {
        var probe = FixedStorageProbe.ForFakeRecorder(fakeRecorder: true, Environment("5000"))!;

        Assert.Equal(probe.Measure(Path.GetTempPath())?.RootPath,
            probe.Measure(Path.Combine(Path.GetTempPath(), "scratch"))?.RootPath);
    }

    [Fact]
    public void ForFakeRecorder_IsIgnoredByTheRealRecorder()
    {
        Assert.Null(FixedStorageProbe.ForFakeRecorder(fakeRecorder: false, Environment("5000")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plenty")]
    [InlineData("-5")]
    public void ForFakeRecorder_MeasuresTheRealDisk_WithoutAUsableValue(string? freeBytes)
    {
        Assert.Null(FixedStorageProbe.ForFakeRecorder(fakeRecorder: true, Environment(freeBytes)));
    }
}
