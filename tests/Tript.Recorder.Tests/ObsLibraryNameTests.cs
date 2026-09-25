// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class ObsLibraryNameTests
{
    [Fact]
    public void OnLinux_TheVersionedSonameIsTriedWhenOnlyTheRuntimePackageIsInstalled() =>
        Assert.Contains("libobs.so.30", ObsLibrary.CandidateFileNames(isWindows: false));

    [Fact]
    public void OnLinux_TheDevelopmentSymlinkIsTheLastResort() =>
        Assert.Equal("libobs.so", ObsLibrary.CandidateFileNames(isWindows: false)[^1]);

    [Fact]
    public void OnWindows_OnlyTheObsDllsAreTried() =>
        Assert.Equal(["obs64.dll", "obs.dll"], ObsLibrary.CandidateFileNames(isWindows: true));
}
