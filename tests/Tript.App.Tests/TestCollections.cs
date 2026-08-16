// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

// The smoke tests each start their own app host child process, and every host binds the same three
// ports (44030 control socket, 2222 content server, 2882 UI host). Only one host can be up at a
// time, so the whole suite is one collection: xunit serializes the classes inside it.
[CollectionDefinition(Name)]
public sealed class AppHostCollection : ICollectionFixture<AppHostCollectionFixture>
{
    public const string Name = "app-host-smoke";
}

// A per-collection fixture that hands each test a unique temp directory. The fixture itself holds
// no process; the driver is per-test so a failure cannot leak a host across tests.
public sealed class AppHostCollectionFixture
{
    internal string NewContentRoot(string testName)
    {
        var path = Path.Combine(Path.GetTempPath(), "tript-app-tests", testName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal string NewSettingsPath(string testName)
    {
        var path = Path.Combine(Path.GetTempPath(), "tript-app-tests", testName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return Path.Combine(path, "settings.json");
    }
}
