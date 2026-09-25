// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class ObsRuntimeLocatorTests
{
    [Theory]
    [InlineData("/usr/lib64", "/usr")]
    [InlineData("/usr/lib", "/usr")]
    [InlineData("/usr/lib/x86_64-linux-gnu", "/usr")]
    [InlineData("/usr/local/lib", "/usr/local")]
    [InlineData("/opt/obs-studio/lib", "/opt/obs-studio")]
    [InlineData("/home/me/obs/build/lib64/", "/home/me/obs/build")]
    public void TheInstallPrefix_IsTheParentOfTheLibraryDirectory(string libDir, string prefix) =>
        Assert.Equal(prefix, ObsRuntimeLocator.InstallPrefixOf(libDir));

    [Fact]
    public void ALibraryDirectoryOutsideAnyLibFolder_HasNoPrefix() =>
        Assert.Null(ObsRuntimeLocator.InstallPrefixOf("/opt/obs-portable"));

    [Fact]
    public void DataIsLookedUpUnderTheLibrarysOwnPrefixBeforeTheSystemOne() =>
        Assert.Equal(
            ["/usr/local/share/obs", "/usr/share/obs"],
            ObsRuntimeLocator.LinuxShareRoots("/usr/local/lib"));

    [Fact]
    public void ASystemLibrary_SearchesTheSystemShareOnce() =>
        Assert.Equal(["/usr/share/obs"], ObsRuntimeLocator.LinuxShareRoots("/usr/lib64"));
}
