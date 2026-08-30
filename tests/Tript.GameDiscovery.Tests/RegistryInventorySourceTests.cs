// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace Tript.GameDiscovery.Tests;

public sealed class RegistryInventorySourceTests
{
    [Fact]
    public async Task EaReadsProductMetadataFromBothRegistryViews()
    {
        using var fixture = new TempFixture();
        var executable = fixture.FilePath("binary", "ea.exe");
        var registry = new FakeRegistry();
        const string parent = @"Software\EA Games";
        registry.SubKeys(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent, "GameKey");
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent + @"\GameKey", "Install Dir", fixture.Root);
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent + @"\GameKey", "ProductId", "ea-product");
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent + @"\GameKey", "DisplayName", "EA Game");

        var inventory = await new EaInventorySource(new PhysicalDiscoveryFileSystem(), registry).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal("ea-product", game.ProductId.Value);
        Assert.Equal("EA Game", game.DisplayName);
        Assert.True(game.TryResolveCatalogueExecutable(new PhysicalDiscoveryFileSystem(), "ea.exe", out var resolved));
        Assert.Equal(executable, resolved);
    }

    [Fact]
    public async Task UbisoftRequiresAnInstallRegistryRecord()
    {
        using var fixture = new TempFixture();
        var executable = fixture.FilePath("binary", "bin", "ubi.exe");
        var registry = new FakeRegistry();
        const string parent = @"Software\Ubisoft\Launcher\Installs";
        registry.SubKeys(RegistryHiveId.LocalMachine, RegistryViewId.Registry64, parent, "123");
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry64, parent + @"\123", "InstallDir", fixture.Root);

        var inventory = await new UbisoftInventorySource(new PhysicalDiscoveryFileSystem(), registry).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal(new ProductId(GameStore.Ubisoft, "123"), game.ProductId);
        Assert.Empty(game.ExecutablePaths);
        Assert.True(game.TryResolveCatalogueExecutable(new PhysicalDiscoveryFileSystem(), @"bin\ubi.exe", out var resolved));
        Assert.Equal(executable, resolved);
    }
}
