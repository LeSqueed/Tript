// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.TestSupport;
using Xunit;

namespace Tript.GameDiscovery.Tests;

public sealed class LinuxSteamInventorySourceTests
{
    [Fact]
    public async Task NativeSteamInstallYieldsGamesUnderSteamappsCommon()
    {
        using var fixture = new TempFixture();
        var home = fixture.DirectoryPath("home");
        var executable = fixture.FilePath("binary", "home", ".local", "share", "Steam", "steamapps", "common", "Overwatch", "Overwatch.exe");
        Manifest(fixture, ["home", ".local", "share", "Steam"], "2357570", "Overwatch", "Overwatch");

        var inventory = await Source(home).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal(new ProductId(GameStore.Steam, "2357570"), game.ProductId);
        Assert.Equal("Overwatch", game.DisplayName);
        Assert.Equal(Path.GetDirectoryName(executable), game.InstallRoot);
        Assert.True(game.TryResolveCatalogueExecutable(new PhysicalDiscoveryFileSystem(), "Overwatch.exe", out var resolved));
        Assert.Equal(executable, resolved);
        Assert.Empty(inventory.Diagnostics);
    }

    [Fact]
    public async Task FlatpakSteamInstallIsFound()
    {
        using var fixture = new TempFixture();
        var home = fixture.DirectoryPath("home");
        string[] flatpak = ["home", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"];
        Manifest(fixture, flatpak, "10", "Flatpak Game", "FlatpakGame");
        fixture.DirectoryPath([.. flatpak, "steamapps", "common", "FlatpakGame"]);

        var inventory = await Source(home).DiscoverAsync();

        Assert.Equal("10", Assert.Single(inventory.Games).ProductId.Value);
    }

    [Fact]
    public async Task LibraryFoldersPointToFurtherLibraries()
    {
        using var fixture = new TempFixture();
        var home = fixture.DirectoryPath("home");
        var external = fixture.DirectoryPath("mnt", "games", "SteamLibrary");
        fixture.FilePath($$"""
            "libraryfolders"
            {
                "0" { "path" "{{fixture.DirectoryPath("home", ".local", "share", "Steam")}}" "apps" { "20" "1" } }
                "1" { "path" "{{external}}" "apps" { "30" "1" } }
            }
            """, "home", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf");
        Manifest(fixture, ["home", ".local", "share", "Steam"], "20", "Home Game", "HomeGame");
        Manifest(fixture, ["mnt", "games", "SteamLibrary"], "30", "External Game", "External Game");
        fixture.DirectoryPath("mnt", "games", "SteamLibrary", "steamapps", "common", "External Game");

        var inventory = await Source(home).DiscoverAsync();

        Assert.Equal(["20", "30"], inventory.Games.Select(game => game.ProductId.Value).Order());
        Assert.Equal(Path.Combine(external, "steamapps", "common", "External Game"),
            inventory.Games.Single(game => game.ProductId.Value == "30").InstallRoot);
    }

    [Fact]
    public async Task ProtonRuntimesAndRedistributablesAreNotGames()
    {
        using var fixture = new TempFixture();
        var home = fixture.DirectoryPath("home");
        string[] steam = ["home", ".local", "share", "Steam"];
        Manifest(fixture, steam, "3658110", "Proton 10.0", "Proton 10.0");
        fixture.FilePath("\"manifest\" { \"commandline\" \"/proton %verb%\" }",
            [.. steam, "steamapps", "common", "Proton 10.0", "toolmanifest.vdf"]);
        Manifest(fixture, steam, "1628350", "Steam Linux Runtime 3.0 (sniper)", "SteamLinuxRuntime_sniper");
        fixture.FilePath("\"manifest\" { \"commandline\" \"/_v2-entry-point --verb=%verb% --\" }",
            [.. steam, "steamapps", "common", "SteamLinuxRuntime_sniper", "toolmanifest.vdf"]);
        Manifest(fixture, steam, "228980", "Steamworks Common Redistributables", "Steamworks Shared");
        fixture.DirectoryPath([.. steam, "steamapps", "common", "Steamworks Shared"]);
        Manifest(fixture, steam, "858710", "Gravity Circuit", "Gravity Circuit");
        fixture.DirectoryPath([.. steam, "steamapps", "common", "Gravity Circuit"]);

        var inventory = await Source(home).DiscoverAsync();

        Assert.Equal("858710", Assert.Single(inventory.Games).ProductId.Value);
    }

    [LinuxFact]
    public async Task LinkedSteamRootsAreReadOnceUnderTheirCanonicalPath()
    {
        using var fixture = new TempFixture();
        var home = fixture.DirectoryPath("home");
        var steam = fixture.DirectoryPath("home", ".local", "share", "Steam");
        fixture.DirectoryPath("home", ".steam");
        Directory.CreateSymbolicLink(Path.Combine(home, ".steam", "steam"), steam);
        Directory.CreateSymbolicLink(Path.Combine(home, ".steam", "root"), steam);
        fixture.FilePath($$"""
            "libraryfolders" { "0" { "path" "{{Path.Combine(home, ".steam", "steam")}}" } }
            """, "home", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf");
        Manifest(fixture, ["home", ".local", "share", "Steam"], "40", "Linked Game", "LinkedGame");
        var fileSystem = new CountingFileSystem();

        var inventory = await SteamInventorySource.ForLinuxHome(fileSystem, home).DiscoverAsync();

        Assert.Equal(Path.Combine(steam, "steamapps", "common", "LinkedGame"), Assert.Single(inventory.Games).InstallRoot);
        Assert.Equal(1, fileSystem.ReadsOf("appmanifest_40.acf"));
        Assert.Equal(1, fileSystem.ReadsOf("libraryfolders.vdf"));
    }

    [Fact]
    public async Task HomeWithoutSteamYieldsNothingAndReportsNothing()
    {
        using var fixture = new TempFixture();

        var inventory = await Source(fixture.DirectoryPath("home")).DiscoverAsync();

        Assert.Empty(inventory.Games);
        Assert.Empty(inventory.Diagnostics);
    }

    [Fact]
    public async Task ManifestEscapingItsLibraryIsReportedAndSkipped()
    {
        using var fixture = new TempFixture();
        var home = fixture.DirectoryPath("home");
        Manifest(fixture, ["home", ".local", "share", "Steam"], "50", "Escaping Game", "../../../../outside");

        var inventory = await Source(home).DiscoverAsync();

        Assert.Empty(inventory.Games);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "steam.manifest");
    }

    private static SteamInventorySource Source(string home) =>
        SteamInventorySource.ForLinuxHome(new PhysicalDiscoveryFileSystem(), home);

    private static string Manifest(TempFixture fixture, string[] steamRoot, string appId, string name, string installDir) =>
        fixture.FilePath($$"""
            "AppState" { "appid" "{{appId}}" "name" "{{name}}" "installdir" "{{installDir}}" }
            """, [.. steamRoot, "steamapps", $"appmanifest_{appId}.acf"]);
}
