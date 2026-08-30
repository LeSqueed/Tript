// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace Tript.GameDiscovery.Tests;

public sealed class XboxInventorySourceTests
{
    [Fact]
    public async Task ConfigDeclaresIdentityDisplayNameAndContainedExecutables()
    {
        using var fixture = new TempFixture();
        var install = fixture.DirectoryPath("XboxGames", "Example");
        var first = fixture.FilePath("binary", "XboxGames", "Example", "Content", "game.exe");
        var second = fixture.FilePath("binary", "XboxGames", "Example", "tools", "launcher.exe");
        fixture.FilePath("""
            <Game>
              <Identity Name="publisher.game" />
              <ShellVisuals DefaultDisplayName="Example Game" />
              <ExecutableList>
                <Executable Name="Content/game.exe" />
                <Executable Name="tools/launcher.exe" />
                <Executable Name="missing.exe" />
                <Executable Name="../unsafe.exe" />
              </ExecutableList>
            </Game>
            """, "XboxGames", "Example", "MicrosoftGame.config");

        var inventory = await new XboxInventorySource(new PhysicalDiscoveryFileSystem(), new FakeDrives(fixture.Root)).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal("publisher.game", game.ProductId.Value);
        Assert.Equal(install, game.InstallRoot);
        Assert.Equal(2, game.ExecutablePaths.Length);
        Assert.Contains(first, game.ExecutablePaths);
        Assert.Contains(second, game.ExecutablePaths);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "xbox.executable");
    }

    [Fact]
    public async Task ContentConfigUsesContentAsCanonicalInstallRoot()
    {
        using var fixture = new TempFixture();
        var content = fixture.DirectoryPath("XboxGames", "Example", "Content");
        var executable = fixture.FilePath("binary", "XboxGames", "Example", "Content", "game.exe");
        fixture.FilePath("""
            <Game>
              <Identity Name="content.game" />
              <ShellVisuals DefaultDisplayName="Content Game" />
              <ExecutableList><Executable Name="game.exe" /></ExecutableList>
            </Game>
            """, "XboxGames", "Example", "Content", "MicrosoftGame.config");

        var inventory = await new XboxInventorySource(new PhysicalDiscoveryFileSystem(), new FakeDrives(fixture.Root)).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal(content, game.InstallRoot);
        Assert.Equal(executable, Assert.Single(game.ExecutablePaths));
    }

    [Fact]
    public async Task ArbitraryFoldersAndFilesAreNotClassified()
    {
        using var fixture = new TempFixture();
        fixture.FilePath("binary", "XboxGames", "NoConfig", "game.exe");

        var inventory = await new XboxInventorySource(new PhysicalDiscoveryFileSystem(), new FakeDrives(fixture.Root)).DiscoverAsync();

        Assert.Empty(inventory.Games);
    }

    [Fact]
    public async Task MalformedConfigProducesDiagnosticWithoutGame()
    {
        using var fixture = new TempFixture();
        fixture.FilePath("<Game>", "XboxGames", "Broken", "MicrosoftGame.config");

        var inventory = await new XboxInventorySource(new PhysicalDiscoveryFileSystem(), new FakeDrives(fixture.Root)).DiscoverAsync();

        Assert.Empty(inventory.Games);
        Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "xbox.config");
    }
}
