// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsModuleTests
{
    [SkippableFact]
    public void LoadedModules_RegisterTheirSourceTypes()
    {
        using var session = ObsSession.Start();

        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720
        });
        Assert.True(session.Runtime.ResetAudio(new ObsAudioSettings()));

        var report = session.StartModules();

        Assert.True(report.AllLoaded, $"Modules failed to load: {string.Join(", ", report.FailedModules)}");

        var inputTypes = session.Runtime.EnumerateInputTypes();
        Assert.Contains("xshm_input", inputTypes);
        Assert.Contains("color_source", inputTypes);
    }

    [SkippableFact]
    public void BeforeModulesLoad_OnlyCoreInputTypesExist()
    {
        using var session = ObsSession.Start();

        var inputTypes = session.Runtime.EnumerateInputTypes();

        Assert.DoesNotContain("xshm_input", inputTypes);
        Assert.DoesNotContain("image_source", inputTypes);
    }

    [SkippableFact]
    public void TheSafeList_KeepsUnlistedModulesOut()
    {
        using var session = ObsSession.Start();
        session.StartModules();

        var inputTypes = session.Runtime.EnumerateInputTypes();

        Assert.DoesNotContain("text_ft2_source_v2", inputTypes);
    }
}
