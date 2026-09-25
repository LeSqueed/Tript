// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.TestSupport;
using Xunit;

namespace Tript.GameDiscovery.Tests;

public sealed class HeroicInventorySourceTests
{
    private static readonly string[] NativeConfig = ["home", ".config", "heroic"];
    private static readonly string[] FlatpakConfig = ["home", ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic"];

    [LinuxFact]
    public async Task LegendaryGameUsesTheCatalogItemIdFromItsMetadata()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "Fortnite");
        var executable = fixture.FilePath("binary", "Games", "Fortnite", "Binaries", "Win64", "Game.exe");
        Legendary(fixture, NativeConfig, $$"""
            {"Fortnite":{"app_name":"Fortnite","title":"Fortnite","install_path":"{{root}}","executable":"Binaries/Win64/Game.exe","is_dlc":false} }
            """);
        fixture.FilePath("""{"app_name":"Fortnite","metadata":{"id":"4fe75bbc5a674f4f9b356b5c90567da5","title":"Fortnite"}}""",
            [.. NativeConfig, "legendaryConfig", "legendary", "metadata", "Fortnite.json"]);

        var inventory = await Source(fixture).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal(new ProductId(GameStore.Epic, "4fe75bbc5a674f4f9b356b5c90567da5"), game.ProductId);
        Assert.Equal("Fortnite", game.DisplayName);
        Assert.Equal(root, game.InstallRoot);
        Assert.Equal(executable, Assert.Single(game.ExecutablePaths));
        Assert.Empty(inventory.Diagnostics);
    }

    [LinuxFact]
    public async Task LegendaryGameWithoutMetadataFallsBackToItsAppName()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "Sugar");
        Legendary(fixture, NativeConfig, $$"""
            {"Sugar":{"app_name":"Sugar","title":"Sugar Game","install_path":"{{root}}","executable":""} }
            """);

        var inventory = await Source(fixture).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal(new ProductId(GameStore.Epic, "Sugar"), game.ProductId);
        Assert.Empty(game.ExecutablePaths);
    }

    [LinuxFact]
    public async Task LegendaryDlcIsNotAGame()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "Base");
        Legendary(fixture, NativeConfig, $$"""
            {
              "Base":{"app_name":"Base","title":"Base","install_path":"{{root}}"},
              "Addon":{"app_name":"Addon","title":"Addon","install_path":"{{root}}","is_dlc":true}
            }
            """);

        var inventory = await Source(fixture).DiscoverAsync();

        Assert.Equal("Base", Assert.Single(inventory.Games).ProductId.Value);
    }

    [LinuxFact]
    public async Task LegendaryExecutableEscapingItsInstallRootIsDropped()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "Escape");
        fixture.FilePath("binary", "Games", "outside.exe");
        Legendary(fixture, NativeConfig, $$"""
            {"Escape":{"app_name":"Escape","title":"Escape","install_path":"{{root}}","executable":"../outside.exe"} }
            """);

        var inventory = await Source(fixture).DiscoverAsync();

        Assert.Empty(Assert.Single(inventory.Games).ExecutablePaths);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "heroic.executable");
    }

    [LinuxFact]
    public async Task GogGameTakesItsTitleFromTheLibrary()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "Witcher3");
        Gog(fixture, NativeConfig, $$"""
            {"installed":[{"appName":"1207664663","install_path":"{{root}}","platform":"windows"}]}
            """);
        fixture.FilePath("""{"games":[{"app_name":"1207664663","title":"The Witcher 3: Wild Hunt"}]}""",
            [.. NativeConfig, "gog_store", "library.json"]);

        var inventory = await Source(fixture).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal(new ProductId(GameStore.Gog, "1207664663"), game.ProductId);
        Assert.Equal(GameStore.Gog, game.Store);
        Assert.Equal("The Witcher 3: Wild Hunt", game.DisplayName);
        Assert.Equal(root, game.InstallRoot);
    }

    [LinuxFact]
    public async Task GogGameMissingFromTheLibraryIsNamedAfterItsInstallFolder()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "Cyberpunk 2077");
        Gog(fixture, NativeConfig, $$"""
            {"installed":[{"appName":"1423049311","install_path":"{{root}}","platform":"windows"}]}
            """);

        var inventory = await Source(fixture).DiscoverAsync();

        Assert.Equal("Cyberpunk 2077", Assert.Single(inventory.Games).DisplayName);
    }

    [LinuxFact]
    public async Task FlatpakHeroicInstallIsFound()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "Hades");
        Gog(fixture, FlatpakConfig, $$"""
            {"installed":[{"appName":"1418100723","install_path":"{{root}}"}]}
            """);

        var inventory = await Source(fixture).DiscoverAsync();

        Assert.Equal(new ProductId(GameStore.Gog, "1418100723"), Assert.Single(inventory.Games).ProductId);
    }

    [LinuxFact]
    public async Task LinkedConfigRootsAreReadOnce()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "Hades");
        var flatpak = fixture.DirectoryPath(FlatpakConfig);
        Gog(fixture, FlatpakConfig, $$"""
            {"installed":[{"appName":"1418100723","install_path":"{{root}}"}]}
            """);
        fixture.DirectoryPath("home", ".config");
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "home", ".config", "heroic"), flatpak);
        var fileSystem = new CountingFileSystem();

        var inventory = await new HeroicInventorySource(fileSystem, Path.Combine(fixture.Root, "home")).DiscoverAsync();

        Assert.Single(inventory.Games);
        Assert.Equal(1, fileSystem.ReadsOf("installed.json"));
    }

    [LinuxFact]
    public async Task MalformedFilesAreReportedAndDoNotHideOtherLibraries()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "Hades");
        Legendary(fixture, NativeConfig, "{ not json");
        Gog(fixture, NativeConfig, $$"""
            {"installed":[{"appName":"1418100723","install_path":"{{root}}"}]}
            """);
        fixture.FilePath("[1, 2", [.. NativeConfig, "gog_store", "library.json"]);

        var inventory = await Source(fixture).DiscoverAsync();

        Assert.Equal(new ProductId(GameStore.Gog, "1418100723"), Assert.Single(inventory.Games).ProductId);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "heroic.legendary" && diagnostic.Store == GameStore.Epic);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "heroic.gog.library" && diagnostic.Store == GameStore.Gog);
    }

    [LinuxFact]
    public async Task GogInstalledFileWithoutAnInstalledArrayIsReported()
    {
        using var fixture = new TempFixture();
        Gog(fixture, NativeConfig, """{"games":[]}""");

        var inventory = await Source(fixture).DiscoverAsync();

        Assert.Empty(inventory.Games);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "heroic.gog");
    }

    [LinuxFact]
    public async Task EntriesWithoutAnAbsoluteInstallPathAreReportedAndSkipped()
    {
        using var fixture = new TempFixture();
        Legendary(fixture, NativeConfig, """{"Relative":{"app_name":"Relative","title":"Relative","install_path":"Games/Relative"}}""");
        Gog(fixture, NativeConfig, """{"installed":[{"appName":"1","install_path":""},{"appName":"2"}]}""");

        var inventory = await Source(fixture).DiscoverAsync();

        Assert.Empty(inventory.Games);
        Assert.Single(inventory.Diagnostics, diagnostic => diagnostic.Code == "heroic.legendary");
        Assert.Equal(2, inventory.Diagnostics.Count(diagnostic => diagnostic.Code == "heroic.gog"));
    }

    [LinuxFact]
    public async Task HomeWithoutHeroicYieldsNothingAndReportsNothing()
    {
        using var fixture = new TempFixture();

        var inventory = await Source(fixture).DiscoverAsync();

        Assert.Empty(inventory.Games);
        Assert.Empty(inventory.Diagnostics);
    }

    private static HeroicInventorySource Source(TempFixture fixture) =>
        new(new PhysicalDiscoveryFileSystem(), fixture.DirectoryPath("home"));

    private static void Legendary(TempFixture fixture, string[] configRoot, string installed) =>
        fixture.FilePath(installed, [.. configRoot, "legendaryConfig", "legendary", "installed.json"]);

    private static void Gog(TempFixture fixture, string[] configRoot, string installed) =>
        fixture.FilePath(installed, [.. configRoot, "gog_store", "installed.json"]);
}
