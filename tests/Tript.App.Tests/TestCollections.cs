// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

[CollectionDefinition(Name)]
public sealed class AppHostCollection : ICollectionFixture<AppHostCollectionFixture>
{
    public const string Name = "app-host-smoke";
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SingleInstanceCollection
{
    public const string Name = "shell-single-instance";
}

public sealed class AppHostCollectionFixture : IDisposable
{
    private static readonly string SuiteRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests");
    private readonly List<string> _roots = [];

    internal string NewContentRoot(string testName)
    {
        var path = Path.Combine(SuiteRoot, testName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _roots.Add(path);
        return path;
    }

    internal string NewSettingsPath(string testName)
    {
        var path = Path.Combine(SuiteRoot, testName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _roots.Add(path);
        return Path.Combine(path, "settings.json");
    }

    public void Dispose()
    {
        foreach (var root in _roots)
            DeleteIfExists(root);
    }

    private static void DeleteIfExists(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
    }
}
