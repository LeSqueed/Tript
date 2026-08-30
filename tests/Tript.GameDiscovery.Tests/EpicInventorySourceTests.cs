// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace Tript.GameDiscovery.Tests;

public sealed class EpicInventorySourceTests
{
    [Fact]
    public async Task ItemAndInstalledDataAreParsedAndUnsafeExecutableIsDropped()
    {
        using var fixture = new TempFixture();
        var manifests = fixture.DirectoryPath("Manifests");
        var gameA = fixture.DirectoryPath("Games", "A");
        var gameB = fixture.DirectoryPath("Games", "B");
        var executable = fixture.FilePath("binary", "Games", "A", "bin", "game.exe");
        fixture.FilePath($$"""{"CatalogItemId":"a","DisplayName":"A","InstallLocation":"{{Json(gameA)}}","LaunchExecutable":"bin/game.exe"}""",
            "Manifests", "a.item");
        var installed = fixture.FilePath($$"""{"InstallationList":[{"AppName":"b","DisplayName":"B","InstallLocation":"{{Json(gameB)}}","LaunchExecutable":"../escape.exe"}]}""",
            "LauncherInstalled.dat");

        var inventory = await new EpicInventorySource(new PhysicalDiscoveryFileSystem(), [manifests, installed]).DiscoverAsync();

        Assert.Equal(2, inventory.Games.Length);
        Assert.Equal(executable, inventory.Games.Single(g => g.ProductId.Value == "a").ExecutablePaths.Single());
        Assert.Empty(inventory.Games.Single(g => g.ProductId.Value == "b").ExecutablePaths);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "epic.executable");
    }

    [Fact]
    public async Task StaleDeclaredExecutableIsDropped()
    {
        using var fixture = new TempFixture();
        var manifests = fixture.DirectoryPath("Manifests");
        var root = fixture.DirectoryPath("Game");
        fixture.FilePath($$"""{"CatalogItemId":"a","DisplayName":"A","InstallLocation":"{{Json(root)}}","LaunchExecutable":"missing.exe"}""",
            "Manifests", "a.item");

        var inventory = await new EpicInventorySource(new PhysicalDiscoveryFileSystem(), [manifests]).DiscoverAsync();

        Assert.Empty(Assert.Single(inventory.Games).ExecutablePaths);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "epic.executable");
    }

    private static string Json(string value) => value.Replace("\\", "\\\\");
}
