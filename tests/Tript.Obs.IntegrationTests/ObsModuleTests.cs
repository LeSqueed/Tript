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

    [SkippableFact]
    public void OpeningAModuleDirectly_RegistersItsTypes()
    {
        using var session = ObsSession.Start();

        var result = session.Runtime.OpenModule(
            $"{ObsTestEnvironment.PluginBinaryPath}/image-source.so",
            ObsTestEnvironment.ModuleDataDir,
            out var module);

        Assert.Equal(ObsModuleOpenResult.Success, result);
        Assert.True(module.IsValid);
        Assert.True(session.Runtime.InitModule(module));

        Assert.Null(session.Runtime.GetModuleName(module));

        Assert.Contains("image_source", session.Runtime.EnumerateInputTypes());
    }

    [SkippableFact]
    public void AMissingModule_IsReportedAsFailedToOpen()
    {
        using var session = ObsSession.Start();

        var result = session.Runtime.OpenModule(
            $"{ObsTestEnvironment.PluginBinaryPath}/tript-no-such-module.so",
            ObsTestEnvironment.ModuleDataDir,
            out var module);

        Assert.Equal(ObsModuleOpenResult.FailedToOpen, result);
        Assert.False(module.IsValid);
    }

    [SkippableFact]
    public void ALibraryThatIsNotAModule_IsReportedRatherThanLoaded()
    {
        using var session = ObsSession.Start();

        var result = session.Runtime.OpenModule(
            "/usr/lib/libobs.so.0",
            ObsTestEnvironment.ModuleDataDir,
            out _);

        Assert.True(result is ObsModuleOpenResult.MissingExports or ObsModuleOpenResult.FailedToOpen,
            $"Expected a missing-exports or failed-to-open status, got {result}.");
    }
}
