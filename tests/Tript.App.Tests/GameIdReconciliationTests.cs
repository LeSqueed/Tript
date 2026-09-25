// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Tript.App.Content;
using Tript.App.Models;
using Tript.App.Resolver;
using Tript.Core;
using Tript.GameDiscovery;
using Tript.Recorder;
using Tript.Settings;
using Xunit;
using Tript.TestSupport;

namespace Tript.App.Tests;

public sealed class GameIdReconciliationTests : IDisposable
{
    private const string CanonicalGameId = "47PHZ983MMRKN9RNXS6V45HWBV";

    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(),
        "tript-app-tests", nameof(GameIdReconciliationTests), Guid.NewGuid().ToString("N"));

    public GameIdReconciliationTests() => Directory.CreateDirectory(_contentRoot);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (AppHost Host, SettingsStore Store, string InstallRoot, string ExecutablePath, ResolveHandler Handler)
        CreateStoreBackedHost(string? resolvedGameId = CanonicalGameId)
    {
        var root = Path.Combine(_contentRoot, Guid.NewGuid().ToString("N"));
        var installRoot = Path.Combine(root, "steamapps", "common", "ExampleGame");
        Directory.CreateDirectory(installRoot);
        var executablePath = Path.Combine(installRoot, "example.exe");
        File.WriteAllText(executablePath, "exe");

        var store = new SettingsStore(new SettingsFileProvider(Path.Combine(root, "settings.json")));
        var handler = new ResolveHandler(resolvedGameId);
        var http = new HttpClient(handler);
        var resolver = new ResolverClient(new ResolverConfig(new Uri("https://resolver.test/"), null), http);
        var host = new AppHost(new AppOptions
        {
            ContentRoot = root,
            SettingsPath = store.FilePath,
            WebRoot = root,
            FakeRecorder = true,
        }, store, runtime: null, new RecordingSessionTracker(), resolverClient: resolver,
            gameIdAliases: new GameIdAliasStore(Path.Combine(root, "game-id-aliases.json")),
            storageProbe: AmpleStorage.Probe);
        host.SetInventoryForTesting(new GameInventory([
            new InstalledGame(GameStore.Steam, new ProductId(GameStore.Steam, "824270"),
                "Example Game", installRoot, ImmutableArray<string>.Empty),
        ], []));

        return (host, store, installRoot, executablePath, handler);
    }

    private static void AddCustomGame(AppHost host, string id, string name, string executablePath,
        bool? autoRecordOverride = null)
    {
        Assert.True(host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            game = new
            {
                gameList = new[] { new { id, name, executablePath, autoRecordOverride } },
            },
        })));
    }

    [Fact]
    public async Task ReconcileCustomGameIdentitiesAsync_RenamesInPlaceAndKeepsOverrides()
    {
        var (host, store, _, executablePath, _) = CreateStoreBackedHost();
        using (host)
        {
            AddCustomGame(host, "custom-abc", "Example Game", executablePath, autoRecordOverride: false);

            await host.ReconcileCustomGameIdentitiesAsync();

            Assert.DoesNotContain(host.GameList, game => game.Id == "custom-abc");
            var migrated = Assert.Single(host.GameList, game => game.Id == CanonicalGameId);
            Assert.Equal("Example Game", migrated.Name);
            Assert.Equal(executablePath, migrated.ExecutablePath);

            var saved = Assert.Single(store.Load().Game.GameList, g => g.Id == CanonicalGameId);
            Assert.False(saved.AutoRecordOverride);
        }
    }

    [Fact]
    public async Task ReconcileCustomGameIdentitiesAsync_WhenCanonicalAlreadyPresent_RemovesStaleEntryWithoutDuplicating()
    {
        var (host, store, installRoot, executablePath, _) = CreateStoreBackedHost();
        using (host)
        {
            var otherExecutablePath = Path.Combine(installRoot, "example-other.exe");
            File.WriteAllText(otherExecutablePath, "exe");
            Assert.True(host.UpdateSettings(JsonSerializer.SerializeToElement(new
            {
                game = new
                {
                    gameList = new[]
                    {
                        new { id = "custom-abc", name = "Duplicate", executablePath },
                        new { id = CanonicalGameId, name = "Example Game", executablePath = otherExecutablePath },
                    },
                },
            })));

            await host.ReconcileCustomGameIdentitiesAsync();

            var remaining = Assert.Single(store.Load().Game.GameList, g => g.Id == CanonicalGameId
                || g.Id == "custom-abc");
            Assert.Equal(CanonicalGameId, remaining.Id);
        }
    }

    [Fact]
    public async Task ReconcileCustomGameIdentitiesAsync_WithoutStoreEvidence_LeavesTheEntryUntouched()
    {
        var (host, store, _, _, handler) = CreateStoreBackedHost();
        using (host)
        {
            var unrootedExecutable = Path.Combine(_contentRoot, "elsewhere", "unrooted.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(unrootedExecutable)!);
            File.WriteAllText(unrootedExecutable, "exe");
            AddCustomGame(host, "custom-abc", "Unrooted Game", unrootedExecutable);

            await host.ReconcileCustomGameIdentitiesAsync();

            Assert.Contains(store.Load().Game.GameList, g => g.Id == "custom-abc");
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task ReconcileCustomGameIdentitiesAsync_WhenResolverFails_LeavesTheEntryUntouched()
    {
        var (host, store, _, executablePath, _) = CreateStoreBackedHost(resolvedGameId: null);
        using (host)
        {
            AddCustomGame(host, "custom-abc", "Example Game", executablePath);

            await host.ReconcileCustomGameIdentitiesAsync();

            Assert.Equal("custom-abc", Assert.Single(store.Load().Game.GameList).Id);
        }
    }

    [Fact]
    public async Task ResolveStoredGameId_AfterMigration_ResolvesAnExistingRecordingsOldIdToTheNewEntry()
    {
        var (host, _, _, executablePath, _) = CreateStoreBackedHost();
        using (host)
        {
            AddCustomGame(host, "custom-abc", "Example Game", executablePath);
            await host.ReconcileCustomGameIdentitiesAsync();

            var sessions = Path.Combine(host.EffectiveRoot, "sessions");
            Directory.CreateDirectory(sessions);
            File.WriteAllText(Path.Combine(sessions, "session-1.mp4"), "recording");
            var metadataStore = new RecordingMetadataStore(Path.Combine(host.EffectiveRoot, "metadata"));
            metadataStore.Save(new RecordingMetadata
            {
                VideoPath = "sessions/session-1.mp4",
                Game = "Example Game",
                GameId = "custom-abc",
            });

            var item = Assert.Single(host.ListContent(), candidate => candidate.FilePath.EndsWith("session-1.mp4"));
            Assert.Equal(CanonicalGameId, item.GameId);
        }
    }

    internal sealed class ResolveHandler(string? gameId) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            if (gameId is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            var content = $$"""{"gameId":"{{gameId}}","canonical":true,"source":"store","displayName":"Example Game"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }
    }
}
