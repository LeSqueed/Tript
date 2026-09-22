// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Tript.App;
using Tript.Shell;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrayIconTests
{
    [SkippableFact]
    public void EveryBadgeCombinationDraws()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Tray icons are drawn with the Windows imaging stack.");

        var path = IconPath();
        foreach (var activity in Enum.GetValues<TrayActivity>())
        foreach (var alert in Enum.GetValues<TrayAlert>())
        {
            var icon = TrayIconFactory.Create(path, 32, activity, alert);
            try
            {
                Assert.NotEqual(IntPtr.Zero, icon);
            }
            finally
            {
                DestroyIcon(icon);
            }
        }
    }

    [SkippableFact]
    public void TheTrayIconSizeIsAPlausibleSmallIconSize()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("SM_CXSMICON is a Windows metric.");

        var size = TrayIconFactory.TrayIconSize();
        Assert.InRange(size, 8, 256);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void TheBadgesStayInsideTheIconAndNeverOverlap(int size)
    {
        var layout = TrayIconFactory.Layout(size);
        var bounds = new System.Drawing.RectangleF(0, 0, size, size);

        Assert.True(bounds.Contains(layout.Status), "the status badge left the icon");
        Assert.True(bounds.Contains(layout.Alert), "the alert badge left the icon");
        Assert.False(layout.Status.IntersectsWith(layout.Alert), "the two badges overlap");
        Assert.True(layout.Outline >= 1f);
    }

    [Fact]
    public void RecordingIsAFilledDotAndBufferingIsARing()
    {
        var recording = TrayIconFactory.Paint(TrayActivity.Recording, TrayAlert.None);
        var buffering = TrayIconFactory.Paint(TrayActivity.Buffering, TrayAlert.None);

        Assert.True(recording.StatusFilled);
        Assert.False(buffering.StatusFilled);
        Assert.NotEqual(recording.Status, buffering.Status);
    }

    [Fact]
    public void AQuietRecorderDrawsNoStatusBadge()
    {
        Assert.Null(TrayIconFactory.Paint(TrayActivity.Idle, TrayAlert.None).Status);
        Assert.Null(TrayIconFactory.Paint(TrayActivity.Detected, TrayAlert.None).Status);
    }

    [Fact]
    public void OnlyARealAlertDrawsTheTriangle()
    {
        Assert.Null(TrayIconFactory.Paint(TrayActivity.Recording, TrayAlert.None).Alert);
        Assert.NotNull(TrayIconFactory.Paint(TrayActivity.Recording, TrayAlert.Warning).Alert);
        Assert.NotNull(TrayIconFactory.Paint(TrayActivity.Recording, TrayAlert.Error).Alert);
        Assert.NotEqual(
            TrayIconFactory.Paint(TrayActivity.Idle, TrayAlert.Warning).Alert,
            TrayIconFactory.Paint(TrayActivity.Idle, TrayAlert.Error).Alert);
    }

    private static string IconPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Tript.Web", "public", "tript.ico");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new SkipException("tript.ico was not found next to the test binary.");
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
