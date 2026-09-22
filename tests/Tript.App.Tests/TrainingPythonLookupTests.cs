// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Tript.App.Training;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrainingPythonLookupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tript-python-lookup-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;

    public TrainingPythonLookupTests()
    {
        _app = Path.Combine(_root, "App");
        Directory.CreateDirectory(_app);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string AddVenv(string folder)
    {
        var python = OperatingSystem.IsWindows()
            ? Path.Combine(folder, ".venv", "Scripts", "python.exe")
            : Path.Combine(folder, ".venv", "bin", "python");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, string.Empty);
        return python;
    }

    [SkippableFact]
    public void InstallRootVenv_WinsOverOneInsideApp_SoUpdatesDoNotLoseIt()
    {
        Skip.If(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRIPT_PYTHON")), "TRIPT_PYTHON is set");
        var rootPython = AddVenv(_root);
        AddVenv(_app);

        Assert.Equal(rootPython, TrainingRunner.FindPython(_app + Path.DirectorySeparatorChar).FileName);
    }

    [SkippableFact]
    public void LegacyAppVenv_IsStillUsed_WhenTheInstallRootHasNone()
    {
        Skip.If(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRIPT_PYTHON")), "TRIPT_PYTHON is set");
        var appPython = AddVenv(_app);

        Assert.Equal(appPython, TrainingRunner.FindPython(_app + Path.DirectorySeparatorChar).FileName);
    }

    [SkippableFact]
    public void NoVenv_FallsBackToSystemPython()
    {
        Skip.If(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRIPT_PYTHON")), "TRIPT_PYTHON is set");

        Assert.Equal("python", TrainingRunner.FindPython(_app + Path.DirectorySeparatorChar).FileName);
    }
}

#endif
