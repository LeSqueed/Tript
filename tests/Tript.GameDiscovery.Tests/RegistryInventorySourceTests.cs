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
    public async Task BattleNetReadsTheUidFromTheBlizzardUninstaller()
    {
        using var fixture = new TempFixture();
        fixture.FilePath("binary", "_retail_", "Overwatch.exe");
        var registry = new FakeRegistry();
        const string parent = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        const string uninstaller = "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS";
        registry.SubKeys(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent,
            "Battle.net", "Overwatch", "Steam App 2357570", "Other");
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent + @"\Battle.net",
            "UninstallString", uninstaller + " --uid=battle.net --displayname=\"Battle.net\"");
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent + @"\Battle.net",
            "InstallLocation", fixture.Root);
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent + @"\Overwatch",
            "UninstallString", uninstaller + " --uid=prometheus --displayname=\"Overwatch\"");
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent + @"\Overwatch",
            "InstallLocation", fixture.Root);
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent + @"\Overwatch",
            "DisplayName", "Overwatch");
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent + @"\Steam App 2357570",
            "UninstallString", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/2357570");
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, parent + @"\Steam App 2357570",
            "InstallLocation", fixture.Root);
        registry.SubKeys(RegistryHiveId.LocalMachine, RegistryViewId.Registry64, parent, "Overwatch");
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry64, parent + @"\Overwatch",
            "UninstallString", uninstaller + " --uid=prometheus");
        registry.Value(RegistryHiveId.LocalMachine, RegistryViewId.Registry64, parent + @"\Overwatch",
            "InstallLocation", fixture.Root);

        var inventory = await new BattleNetInventorySource(new PhysicalDiscoveryFileSystem(), registry).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal(new ProductId(GameStore.BattleNet, "prometheus"), game.ProductId);
        Assert.Equal("battlenet:prometheus", game.ProductId.ToString());
        Assert.Equal("Overwatch", game.DisplayName);
        Assert.True(game.TryResolveCatalogueExecutable(new PhysicalDiscoveryFileSystem(), @"_retail_\Overwatch.exe", out _));
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
