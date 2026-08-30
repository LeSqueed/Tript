// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace Tript.GameDiscovery.Tests;

public sealed class XboxPackageInventorySourceTests
{
    [Fact]
    public async Task PackageMetadataPreservesEveryExistingDeclaredExecutable()
    {
        using var fixture = new TempFixture();
        var first = fixture.FilePath("binary", "game.exe");
        var second = fixture.FilePath("binary", "bin", "helper.exe");
        var package = new XboxPackage("Publisher.Game", "Package Game", fixture.Root,
            ["game.exe", @"bin\helper.exe", "missing.exe"]);

        var inventory = await new XboxPackageInventorySource(
            new PhysicalDiscoveryFileSystem(), new FakeXboxPackages(package)).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal(2, game.ExecutablePaths.Length);
        Assert.Contains(first, game.ExecutablePaths);
        Assert.Contains(second, game.ExecutablePaths);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "xbox.package.executable");
    }

    [Fact]
    public async Task CancellationIsCheckedWhileProcessingPackages()
    {
        using var fixture = new TempFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var package = new XboxPackage("id", "name", fixture.Root, []);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new XboxPackageInventorySource(new PhysicalDiscoveryFileSystem(), new FakeXboxPackages(package))
                .DiscoverAsync(cancellation.Token));
    }
}
