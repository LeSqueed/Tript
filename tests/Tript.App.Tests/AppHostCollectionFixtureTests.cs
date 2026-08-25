// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class AppHostCollectionFixtureTests
{
    [Fact]
    public void Dispose_RemovesOnlyTheTempRootsItCreated()
    {
        var fixture = new AppHostCollectionFixture();
        var contentRoot = fixture.NewContentRoot(nameof(Dispose_RemovesOnlyTheTempRootsItCreated));
        var settingsPath = fixture.NewSettingsPath(nameof(Dispose_RemovesOnlyTheTempRootsItCreated));
        var settingsRoot = Path.GetDirectoryName(settingsPath)!;
        var sentinelRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests", "sentinel");

        File.WriteAllText(Path.Combine(contentRoot, "content.txt"), "content");
        File.WriteAllText(settingsPath, "{}");
        Directory.CreateDirectory(sentinelRoot);
        File.WriteAllText(Path.Combine(sentinelRoot, "secret.txt"), "secret");

        fixture.Dispose();

        Assert.False(Directory.Exists(contentRoot));
        Assert.False(Directory.Exists(settingsRoot));
        Assert.True(Directory.Exists(sentinelRoot));
        Directory.Delete(sentinelRoot, recursive: true);
    }
}
