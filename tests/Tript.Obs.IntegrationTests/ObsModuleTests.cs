// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsModuleTests
{
    [Fact]
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

        // A loaded module that registered nothing is indistinguishable from one that never loaded
        // if all you count is modules, so the assertion is on what they registered.
        var inputTypes = session.Runtime.EnumerateInputTypes();
        Assert.Contains("xshm_input", inputTypes);
        Assert.Contains("color_source", inputTypes);
    }

    [Fact]
    public void BeforeModulesLoad_OnlyCoreInputTypesExist()
    {
        using var session = ObsSession.Start();

        var inputTypes = session.Runtime.EnumerateInputTypes();

        // Not empty: libobs registers audio_line itself, before any module is involved. Everything
        // else comes from a plugin.
        Assert.DoesNotContain("xshm_input", inputTypes);
        Assert.DoesNotContain("image_source", inputTypes);
    }

    // The safe list is an allowlist. Without it libobs loads everything it finds, which on a machine
    // with a full OBS install includes plugins that call into a frontend that is not there and abort
    // the process. The tests depend on it working, so it is asserted rather than assumed.
    [Fact]
    public void TheSafeList_KeepsUnlistedModulesOut()
    {
        using var session = ObsSession.Start();
        session.StartModules();

        var inputTypes = session.Runtime.EnumerateInputTypes();

        // text-freetype2 is present on this machine and deliberately not on the safe list.
        Assert.DoesNotContain("text_ft2_source_v2", inputTypes);
    }

    [Fact]
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

        // The name comes from the module's optional obs_module_name export, so null is a legitimate
        // answer and not a marshalling failure — image-source does not implement it.
        Assert.Null(session.Runtime.GetModuleName(module));

        Assert.Contains("image_source", session.Runtime.EnumerateInputTypes());
    }

    // The module return codes, mapped rather than collapsed to a bool. "Not there" and "there but
    // built against a different libobs" need different messages to a user.
    [Fact]
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

    [Fact]
    public void ALibraryThatIsNotAModule_IsReportedRatherThanLoaded()
    {
        using var session = ObsSession.Start();

        // libobs itself loads as a shared object but exports none of the module entry points.
        var result = session.Runtime.OpenModule(
            "/usr/lib/libobs.so.0",
            ObsTestEnvironment.ModuleDataDir,
            out _);

        Assert.True(result is ObsModuleOpenResult.MissingExports or ObsModuleOpenResult.FailedToOpen,
            $"Expected a missing-exports or failed-to-open status, got {result}.");
    }
}
