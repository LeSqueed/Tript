// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace Tript.GameDiscovery.Tests;

public sealed class SteamInventorySourceTests
{
    [Fact]
    public async Task ModernAndLegacyLibrariesYieldManifestGamesOnly()
    {
        using var fixture = new TempFixture();
        var steam = fixture.DirectoryPath("Steam");
        var modern = fixture.DirectoryPath("Modern");
        var legacy = fixture.DirectoryPath("Legacy");
        fixture.DirectoryPath("Steam", "steamapps");
        fixture.DirectoryPath("Modern", "steamapps", "common");
        fixture.DirectoryPath("Legacy", "steamapps", "common");
        var modernExecutable = fixture.FilePath("binary", "Modern", "steamapps", "common", "ModernGame", "modern.exe");
        fixture.DirectoryPath("Legacy", "steamapps", "common", "LegacyGame");
        fixture.FilePath($$"""
            "libraryfolders"
            {
                "0" { "path" "{{modern.Replace("\\", "\\\\")}}" }
                "1" "{{legacy.Replace("\\", "\\\\")}}"
            }
            """, "Steam", "steamapps", "libraryfolders.vdf");
        fixture.FilePath("""
            "AppState" { "appid" "10" "name" "Modern Game" "installdir" "ModernGame" }
            """, "Modern", "steamapps", "appmanifest_10.acf");
        fixture.FilePath("""
            "AppState" { "appid" "20" "name" "Legacy Game" "installdir" "LegacyGame" }
            """, "Legacy", "steamapps", "appmanifest_20.acf");
        fixture.FilePath("not a manifest", "Modern", "steamapps", "random.txt");
        var registry = new FakeRegistry();
        registry.Value(RegistryHiveId.CurrentUser, RegistryViewId.Registry64,
            @"Software\Valve\Steam", "SteamPath", steam);

        var inventory = await new SteamInventorySource(new PhysicalDiscoveryFileSystem(), registry).DiscoverAsync();

        Assert.Equal(["10", "20"], inventory.Games.Select(game => game.ProductId.Value).Order());
        Assert.All(inventory.Games, game => Assert.Empty(game.ExecutablePaths));
        var game = inventory.Games.Single(item => item.ProductId.Value == "10");
        Assert.True(game.TryResolveCatalogueExecutable(new PhysicalDiscoveryFileSystem(), "modern.exe", out var resolved));
        Assert.Equal(modernExecutable, resolved);
    }

    [Fact]
    public async Task MalformedManifestDoesNotHideValidManifest()
    {
        using var fixture = new TempFixture();
        var steam = fixture.DirectoryPath("Steam");
        fixture.DirectoryPath("Steam", "steamapps", "common");
        fixture.FilePath("broken", "Steam", "steamapps", "appmanifest_1.acf");
        fixture.FilePath("\"AppState\" { \"appid\" \"2\" \"name\" \"Good\" \"installdir\" \"Good\" }",
            "Steam", "steamapps", "appmanifest_2.acf");
        var registry = new FakeRegistry();
        registry.Value(RegistryHiveId.CurrentUser, RegistryViewId.Registry32,
            @"Software\Valve\Steam", "SteamPath", steam);

        var inventory = await new SteamInventorySource(new PhysicalDiscoveryFileSystem(), registry).DiscoverAsync();

        Assert.Equal("2", Assert.Single(inventory.Games).ProductId.Value);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "steam.manifest");
    }

    [Fact]
    public async Task CancellationIsCheckedBeforeManifestParsing()
    {
        using var fixture = new TempFixture();
        var steam = fixture.DirectoryPath("Steam");
        fixture.DirectoryPath("Steam", "steamapps", "common");
        fixture.FilePath("broken", "Steam", "steamapps", "appmanifest_1.acf");
        var registry = new FakeRegistry();
        registry.Value(RegistryHiveId.CurrentUser, RegistryViewId.Registry32,
            @"Software\Valve\Steam", "SteamPath", steam);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new SteamInventorySource(new PhysicalDiscoveryFileSystem(), registry).DiscoverAsync(cancellation.Token));
    }

    [Fact]
    public async Task RegistryInstallKeepsListingSteamworksRedistributables()
    {
        using var fixture = new TempFixture();
        var steam = fixture.DirectoryPath("Steam");
        fixture.DirectoryPath("Steam", "steamapps", "common", "Steamworks Shared");
        fixture.FilePath("\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" \"installdir\" \"Steamworks Shared\" }",
            "Steam", "steamapps", "appmanifest_228980.acf");
        var registry = new FakeRegistry();
        registry.Value(RegistryHiveId.CurrentUser, RegistryViewId.Registry64,
            @"Software\Valve\Steam", "SteamPath", steam);

        var inventory = await new SteamInventorySource(new PhysicalDiscoveryFileSystem(), registry).DiscoverAsync();

        Assert.Equal("228980", Assert.Single(inventory.Games).ProductId.Value);
    }
}
