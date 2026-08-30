// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace Tript.GameDiscovery.Tests;

public sealed class PathSafetyTests
{
    private readonly PhysicalDiscoveryFileSystem _fileSystem = new();

    [Fact]
    public void RelativeChildIsCanonicalizedInsideRoot()
    {
        using var fixture = new TempFixture();
        Assert.True(PathSafety.TryResolveLexicallyContained(_fileSystem, fixture.Root, @"bin\game.exe", out var result));
        Assert.Equal(Path.Combine(fixture.Root, "bin", "game.exe"), result);
    }

    [Theory]
    [InlineData(@"..\outside.exe")]
    [InlineData(@"bin\..\..\outside.exe")]
    public void TraversalIsRejected(string candidate)
    {
        using var fixture = new TempFixture();
        Assert.False(PathSafety.TryResolveLexicallyContained(_fileSystem, fixture.Root, candidate, out _));
    }

    [Fact]
    public void SiblingWithRootPrefixIsRejected()
    {
        using var fixture = new TempFixture();
        Assert.False(PathSafety.TryResolveLexicallyContained(_fileSystem, fixture.Root, fixture.Root + "-other\\game.exe", out _));
    }

    [Fact]
    public void CatalogueExecutableMustExistUnderInstallRoot()
    {
        using var fixture = new TempFixture();
        var executable = fixture.FilePath("binary", "bin", "game.exe");
        var game = new InstalledGame(GameStore.Steam, new(GameStore.Steam, "1"), "Game", fixture.Root, []);

        Assert.True(game.TryResolveCatalogueExecutable(_fileSystem, @"bin\game.exe", out var resolved));
        Assert.Equal(executable, resolved);
        Assert.False(game.TryResolveCatalogueExecutable(_fileSystem, "missing.exe", out _));
        Assert.False(game.TryResolveCatalogueExecutable(_fileSystem, @"..\outside.exe", out _));
    }
}
