// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Updater;
using Xunit;

namespace Tript.App.Tests;

public sealed class UpdateStagingPathsTests
{
    [Fact]
    public void InstallRootFromAppBaseDirectory_ReturnsTheParentOfApp()
    {
        var installRoot = Path.Combine(Path.GetTempPath(), "tript-install-root-" + Guid.NewGuid().ToString("N"));
        var appDirectory = Path.Combine(installRoot, "App");

        Assert.Equal(Path.GetFullPath(installRoot),
            UpdateStagingPaths.InstallRootFromAppBaseDirectory(appDirectory));
    }

    [Fact]
    public void InstallRootFromAppBaseDirectory_IgnoresATrailingSeparator()
    {
        var installRoot = Path.Combine(Path.GetTempPath(), "tript-install-root-" + Guid.NewGuid().ToString("N"));
        var appDirectory = Path.Combine(installRoot, "App") + Path.DirectorySeparatorChar;

        Assert.Equal(Path.GetFullPath(installRoot),
            UpdateStagingPaths.InstallRootFromAppBaseDirectory(appDirectory));
    }

    [Fact]
    public void StagedFolderPath_IsUnderTheStagingRoot()
    {
        var installRoot = Path.Combine(Path.GetTempPath(), "tript-install-root-" + Guid.NewGuid().ToString("N"));

        var staged = UpdateStagingPaths.StagedFolderPath(installRoot, "staged-abc");

        Assert.Equal(Path.Combine(UpdateStagingPaths.StagingRoot(installRoot), "staged-abc"), staged);
    }
}
