// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace Tript.GameDiscovery.Tests;

public sealed class GameDiscoveryServiceTests
{
    [Fact]
    public async Task FailedSourceDoesNotPreventOtherSources()
    {
        var game = new InstalledGame(GameStore.Steam, new(GameStore.Steam, "1"), "Game", @"C:\Game", []);
        var service = new GameDiscoveryService([new ThrowingSource(GameStore.Epic), new StaticSource(game)]);

        var inventory = await service.DiscoverAsync();

        Assert.Equal(game, Assert.Single(inventory.Games));
        var diagnostic = Assert.Single(inventory.Diagnostics);
        Assert.Equal(GameStore.Epic, diagnostic.Store);
        Assert.Equal("source.failed", diagnostic.Code);
    }

    [Fact]
    public async Task ProductIdsMergeCaseInsensitivelyAndRetainRicherMetadata()
    {
        var sparse = new InstalledGame(GameStore.EA, new(GameStore.EA, "PRODUCT"), "Game", @"C:\G", [@"C:\G\a.exe"]);
        var rich = new InstalledGame(GameStore.EA, new(GameStore.EA, "product"), "The Game", @"C:\Games\TheGame",
            [@"C:\Games\TheGame\b.exe", @"C:\G\A.EXE"]);

        var inventory = await new GameDiscoveryService([new StaticSource(sparse), new StaticSource(rich)]).DiscoverAsync();

        var game = Assert.Single(inventory.Games);
        Assert.Equal("The Game", game.DisplayName);
        Assert.Equal(@"C:\Games\TheGame", game.InstallRoot);
        Assert.Equal(2, game.ExecutablePaths.Length);
        Assert.Equal(new ProductId(GameStore.EA, "product"), new ProductId(GameStore.EA, "PRODUCT"));
    }

    [Fact]
    public async Task CancellationStopsDiscoveryBeforeSourceWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var game = new InstalledGame(GameStore.Steam, new(GameStore.Steam, "1"), "Game", @"C:\Game", []);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new GameDiscoveryService([new StaticSource(game)]).DiscoverAsync(cancellation.Token));
    }
}
