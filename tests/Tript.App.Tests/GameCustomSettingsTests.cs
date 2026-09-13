// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using Tript.App.Content;
using Tript.App.Resolver;
using Tript.Core;
using Tript.GameDiscovery;
using Tript.Recorder;
using Tript.Settings;
using Xunit;
using Tript.TestSupport;

namespace Tript.App.Tests;

public sealed class GameCustomSettingsTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly SettingsStore _store;
    private readonly AppHost _host;

    public GameCustomSettingsTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(GameCustomSettingsTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        var settingsPath = Path.Combine(_contentRoot, "settings.json");
        _store = new SettingsStore(new SettingsFileProvider(settingsPath));

        _host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = settingsPath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, _store, runtime: null, new RecordingSessionTracker());
    }

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ExecutablePath(string name) => Path.Combine(_contentRoot, name);

    [Fact]
    public void AValidCustomGame_SurvivesSaveAndReload()
    {
        var exe = ExecutablePath("doom.exe");
        File.WriteAllText(exe, "not a real PE; only the path matters");
        try
        {
            Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
            {
                game = new
                {
                    gameList = new[]
                    {
                        new { id = "custom-doom", name = "Doom", executablePath = exe },
                    },
                },
            })));

            var game = Assert.Single(_host.GameList, candidate => candidate.Id == "custom-doom");
            Assert.False(game.BuiltIn);
            Assert.Equal("Doom", game.Name);
            Assert.Equal(Path.GetFileName(exe), game.Executable);
            Assert.Equal(exe, game.ExecutablePath);

            var onDisk = JsonDocument.Parse(File.ReadAllText(_store.FilePath));
            var saved = onDisk.RootElement.GetProperty("game").GetProperty("gameList")[0];
            Assert.Equal("custom-doom", saved.GetProperty("id").GetString());
            Assert.Equal(exe, saved.GetProperty("executablePath").GetString());
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void AutoRecordOverride_OnlyChangesTheGlobalDefaultWhenPresent()
    {
        var settings = _store.Load();
        var game = Assert.Single(settings.Game.GameList);

        Assert.True(_host.ShouldAutoRecord(game.Id));
        game.AutoRecordOverride = false;
        Assert.False(_host.ShouldAutoRecord(game.Id));

        settings.Game.AutoRecordDetectedGames = false;
        game.AutoRecordOverride = null;
        Assert.False(_host.ShouldAutoRecord(game.Id));
        game.AutoRecordOverride = true;
        Assert.True(_host.ShouldAutoRecord(game.Id));
    }

    [Fact]
    public void FullscreenCandidate_IsResolvedAndAddedWithItsCanonicalId()
    {
        var root = Path.Combine(_contentRoot, "resolved");
        var installRoot = Path.Combine(root, "steamapps", "common", "ExampleGame");
        Directory.CreateDirectory(installRoot);
        var executablePath = Path.Combine(installRoot, "example.exe");
        File.WriteAllText(executablePath, "exe");
        var store = new SettingsStore(new SettingsFileProvider(Path.Combine(root, "settings.json")));
        var handler = new ResolverHandler();
        using var http = new HttpClient(handler);
        using var resolver = new ResolverClient(new ResolverConfig(new Uri("https://resolver.test/"), null), http);
        using var host = new AppHost(new AppOptions
        {
            ContentRoot = root,
            SettingsPath = store.FilePath,
            WebRoot = root,
            FakeRecorder = true,
        }, store, runtime: null, new RecordingSessionTracker(), resolverClient: resolver);
        host.SetInventoryForTesting(new GameInventory([
            new InstalledGame(GameStore.Steam, new ProductId(GameStore.Steam, "824270"),
                "Example Game", installRoot, ImmutableArray<string>.Empty),
        ], []));

        host.OnFullscreenCandidateFound(new FullscreenGameCandidate(42, "example.exe", executablePath));

        Assert.True(SpinWait.SpinUntil(() => host.GameList.Any(game => game.Id == ResolverHandler.GameId),
            TimeSpan.FromSeconds(3)));
        var game = Assert.Single(store.Load().Game.GameList, value => value.Id == ResolverHandler.GameId);
        Assert.Equal("Example Game", game.Name);
        Assert.Equal(executablePath, game.ExecutablePath);
        Assert.Equal("/resolve?input=steam%3A824270", Assert.Single(handler.Requests));
    }

    [Fact]
    public void FullscreenCandidate_WithoutStoreEvidence_IsNotAddedAndDoesNotQueryTheResolver()
    {
        var root = Path.Combine(_contentRoot, "unresolved");
        Directory.CreateDirectory(root);
        var executablePath = Path.Combine(root, "example.exe");
        File.WriteAllText(executablePath, "exe");
        var store = new SettingsStore(new SettingsFileProvider(Path.Combine(root, "settings.json")));
        var handler = new ResolverHandler();
        using var http = new HttpClient(handler);
        using var resolver = new ResolverClient(new ResolverConfig(new Uri("https://resolver.test/"), null), http);
        using var host = new AppHost(new AppOptions
        {
            ContentRoot = root,
            SettingsPath = store.FilePath,
            WebRoot = root,
            FakeRecorder = true,
        }, store, runtime: null, new RecordingSessionTracker(), resolverClient: resolver);

        host.OnFullscreenCandidateFound(new FullscreenGameCandidate(42, "example.exe", executablePath));

        Assert.DoesNotContain(host.GameList, game => game.Id == ResolverHandler.GameId);
        Assert.DoesNotContain(store.Load().Game.GameList, value => value.Id == ResolverHandler.GameId);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void FullscreenCandidate_UsesTheContainingSteamProductIdentity()
    {
        var installRoot = Path.Combine(_contentRoot, "steamapps", "common", "FPSAimTrainer");
        var executablePath = Path.Combine(installRoot, "FPSAimTrainer", "Binaries", "Win64",
            "FPSAimTrainer-Win64-Shipping.exe");
        var inventory = new GameInventory([
            new InstalledGame(GameStore.Steam, new ProductId(GameStore.Steam, "824270"),
                "KovaaK's", installRoot, ImmutableArray<string>.Empty),
        ], []);
        var candidate = new FullscreenGameCandidate(42, "FPSAimTrainer-Win64-Shipping.exe", executablePath);

        var resolution = AppHost.CandidateResolverInput(candidate, executablePath, inventory);
        Assert.Equal("steam:824270", resolution.Input);
        Assert.True(resolution.StoreBacked);

        var unrooted = AppHost.CandidateResolverInput(candidate,
            Path.Combine(_contentRoot, "elsewhere", "example.exe"), new GameInventory([], []));
        Assert.Equal("executable:FPSAimTrainer-Win64-Shipping", unrooted.Input);
        Assert.False(unrooted.StoreBacked);
    }

    [Fact]
    public async Task GameSearch_SearchesThenResolvesOnlyTheSelectedResult()
    {
        var handler = new ResolverHandler();
        using var http = new HttpClient(handler);
        using var resolver = new ResolverClient(new ResolverConfig(new Uri("https://resolver.test/"), null), http);
        using var host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = _store.FilePath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, _store, runtime: null, new RecordingSessionTracker(), resolverClient: resolver);
        var messages = new List<(string Method, JsonElement Content)>();
        var client = new ClientHandle((method, content) => messages.Add((method, content)));

        await host.SearchGamesAsync(new SearchGamesParameters
        {
            RequestId = "search-1",
            Query = "Example",
        }, client);
        await host.ResolveGameSearchAsync(new ResolveGameSearchParameters
        {
            RequestId = "resolve-1",
            Input = "igdb:456",
        }, client);

        Assert.Equal(["/search?q=Example&limit=20", "/resolve?input=igdb%3A456"], handler.Requests);
        Assert.Equal("gameSearchResults", messages[0].Method);
        Assert.Equal(456, messages[0].Content.GetProperty("results")[0].GetProperty("igdbId").GetInt64());
        Assert.Equal("gameSearchResolved", messages[1].Method);
        Assert.Equal(ResolverHandler.GameId,
            messages[1].Content.GetProperty("game").GetProperty("gameId").GetString());
    }

    [Fact]
    public async Task RequestGameAddAsync_WithoutAResolverConfigured_PushesRejected()
    {
        var messages = new List<(string Method, JsonElement Content)>();
        var client = new ClientHandle((method, content) => messages.Add((method, content)));

        await _host.RequestGameAddAsync(new RequestGameAddParameters
        {
            RequestId = "req-1",
            GameId = "some-game",
        }, client);

        var message = Assert.Single(messages);
        Assert.Equal("gameAddRequested", message.Method);
        Assert.Equal("rejected", message.Content.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("accepted-game", "accepted")]
    [InlineData("duplicate-game", "alreadyRequested")]
    [InlineData("limited-game", "rateLimited")]
    public async Task RequestGameAddAsync_ForwardsTheResolverOutcome(string gameId, string expectedStatus)
    {
        var handler = new RequestGameHandler();
        using var http = new HttpClient(handler);
        using var resolver = new ResolverClient(new ResolverConfig(new Uri("https://resolver.test/"), null), http);
        using var host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = _store.FilePath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, _store, runtime: null, new RecordingSessionTracker(), resolverClient: resolver);
        var messages = new List<(string Method, JsonElement Content)>();
        var client = new ClientHandle((method, content) => messages.Add((method, content)));

        await host.RequestGameAddAsync(new RequestGameAddParameters
        {
            RequestId = "req-1",
            GameId = gameId,
        }, client);

        var message = Assert.Single(messages);
        Assert.Equal("gameAddRequested", message.Method);
        Assert.Equal(expectedStatus, message.Content.GetProperty("status").GetString());
        if (expectedStatus == "rateLimited")
            Assert.Equal(21600, message.Content.GetProperty("retryAfterSeconds").GetInt32());
    }

    private sealed class RequestGameHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("duplicate-game", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"already_requested"}""", Encoding.UTF8, "application/json"),
                });
            }
            if (path.Contains("limited-game", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("""{"status":"rate_limited","retryAfterSeconds":21600}""",
                        Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"accepted"}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void AnInvalidCustomGameUpdate_IsRejectedWithoutSaving()
    {
        var before = _store.Load().Game.GameList.ToList();

        Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            game = new
            {
                gameList = new[]
                {
                    new { id = "custom-doom", name = "Doom", executablePath = "" },
                },
            },
        })));

        Assert.DoesNotContain(_host.GameList, game => game.Id == "custom-doom");
        Assert.Equal(before.Select(g => g.Id), _store.Load().Game.GameList.Select(g => g.Id));
    }

    [Fact]
    public void ValidateGameList_RejectsDuplicateCustomExecutablePaths()
    {
        var exe = ExecutablePath("quake.exe");
        Assert.False(_host.ValidateGameList(
        [
            new GameSetting { Id = "custom-doom", Name = "Doom", ExecutablePath = exe },
            new GameSetting { Id = "custom-quake", Name = "Quake", ExecutablePath = exe },
        ], out var failure));
        Assert.Contains("same executable", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateGameList_AllowsDuplicateCustomExecutableNamesAtDifferentPaths()
    {
        Assert.True(_host.ValidateGameList(
        [
            new GameSetting { Id = "custom-a", Name = "A", ExecutablePath = @"C:\Games\A\shared.exe" },
            new GameSetting { Id = "custom-b", Name = "B", ExecutablePath = @"C:\Games\B\shared.exe" },
        ], out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void ValidateGameList_RejectsAnExecutablePathOnAPackagedGame()
    {
        Assert.False(_host.ValidateGameList(
        [
            new GameSetting { Id = "Overwatch", Name = "Overwatch", ExecutablePath = @"C:\Games\Overwatch\Overwatch.exe" },
        ], out var failure));
        Assert.Contains("packaged", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARelativeExecutablePath_IsRejected()
    {
        Assert.False(_host.ValidateGameList(
        [
            new GameSetting { Id = "custom-doom", Name = "Doom", ExecutablePath = @"Games\doom.exe" },
        ], out _));
    }

    [Fact]
    public void AddGameCandidate_PersistsAnExactPathCustomGame()
    {
        var exe = ExecutablePath("suggested.exe");
        File.WriteAllText(exe, "suggestion");
        try
        {
            _host.AddGameCandidate("Suggested", exe);

            var game = Assert.Single(_host.GameList, candidate => candidate.ExecutablePath == exe);
            Assert.StartsWith("custom-", game.Id);
            Assert.Equal("Suggested", game.Name);
            Assert.False(game.BuiltIn);
            Assert.Contains(exe, _store.Load().Game.GameList.Select(g => g.ExecutablePath));
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void AddGameCandidate_RefusesAMissingExecutable()
    {
        _host.AddGameCandidate("Ghost", Path.Combine(_contentRoot, "gone.exe"));

        Assert.DoesNotContain(_host.GameList, game => game.Id.StartsWith("custom-"));
    }

    [WindowsFact]
    public void IgnoreGameCandidate_PersistsTheApplicationPath()
    {
        var path = @"C:\Tools\some.bin";

        _host.IgnoreGameCandidate(path);

        Assert.Contains(path, _store.Load().Game.IgnoredApplications, FilePaths.Comparer);
        Assert.Contains(path, new SettingsStore(new SettingsFileProvider(_store.FilePath))
            .Load().Game.IgnoredApplications, FilePaths.Comparer);
        Assert.DoesNotContain(_host.GameList, game => game.Id.StartsWith("custom-"));
    }

    [LinuxFact]
    public void IgnoreGameCandidate_PersistsTheApplicationPath_OnLinux()
    {
        var path = Path.Combine(Path.GetTempPath(), "tript-ignore-app-test.bin");

        _host.IgnoreGameCandidate(path);

        Assert.Contains(path, _store.Load().Game.IgnoredApplications, FilePaths.Comparer);
        Assert.DoesNotContain(_host.GameList, game => game.Id.StartsWith("custom-"));
    }

    private sealed class ResolverHandler : HttpMessageHandler
    {
        internal const string GameId = "01HRESOLVEDGAME000000000000";
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);
            var content = path.StartsWith("/search", StringComparison.Ordinal)
                ? """{"results":[{"name":"Example Game","source":"igdb","igdbId":456}]}"""
                : $$"""{"gameId":"{{GameId}}","canonical":true,"source":"store","displayName":"Example Game"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }
    }

    [WindowsFact]
    public void IgnoreGameCandidate_DoesNotDuplicateTheSamePath()
    {
        _host.IgnoreGameCandidate(@"C:\Tools\some.bin");
        _host.IgnoreGameCandidate(@"c:\tools\SOME.bin");

        Assert.Single(_store.Load().Game.IgnoredApplications);
    }

    [LinuxFact]
    public void IgnoreGameCandidate_DoesNotDuplicateTheSamePath_OnLinux()
    {
        var path = Path.Combine(Path.GetTempPath(), "tript-ignore-app-dedupe-test.bin");

        _host.IgnoreGameCandidate(path);
        _host.IgnoreGameCandidate(path);

        Assert.Single(_store.Load().Game.IgnoredApplications);
    }

    [Fact]
    public void RenamingACustomGame_UpdatesExistingLibraryItems_OnTheNextListing()
    {
        var exe = ExecutablePath("forza.exe");
        File.WriteAllText(exe, "not a real PE; only the path matters");

        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "session-1.mp4"), "recording");
        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-1.mp4",
            Game = "Forza",
            GameId = "custom-forza",
        });

        try
        {
            Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
            {
                game = new
                {
                    gameList = new[]
                    {
                        new { id = "custom-forza", name = "Forza", executablePath = exe },
                    },
                },
            })));

            var before = Assert.Single(_host.ListContent(), item => item.GameId == "custom-forza");
            Assert.Equal("Forza", before.Game);

            Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
            {
                game = new
                {
                    gameList = new[]
                    {
                        new { id = "custom-forza", name = "Forza Horizon 6", executablePath = exe },
                    },
                },
            })));

            var game = Assert.Single(_host.GameList, candidate => candidate.Id == "custom-forza");
            Assert.Equal("Forza Horizon 6", game.Name);

            var after = Assert.Single(_host.ListContent(), item => item.GameId == "custom-forza");
            Assert.Equal("Forza Horizon 6", after.Game);
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void RemovingACustomGame_LeavesExistingLibraryItems_WithTheirLastKnownName()
    {
        var exe = ExecutablePath("forza.exe");
        File.WriteAllText(exe, "not a real PE; only the path matters");

        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "session-1.mp4"), "recording");
        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-1.mp4",
            Game = "Forza",
            GameId = "custom-forza",
        });

        try
        {
            Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
            {
                game = new
                {
                    gameList = new[]
                    {
                        new { id = "custom-forza", name = "Forza", executablePath = exe },
                    },
                },
            })));

            Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
            {
                game = new
                {
                    gameList = Array.Empty<object>(),
                },
            })));

            var item = Assert.Single(_host.ListContent(), candidate => candidate.GameId == "custom-forza");
            Assert.Equal("Forza", item.Game);
        }
        finally
        {
            File.Delete(exe);
        }
    }

#if TRIPT_TRAINING
    [Fact]
    public async Task TrainingList_ForAGameWithoutEventDefinitions_DoesNotThrow()
    {
        var exe = ExecutablePath("custom.exe");
        File.WriteAllText(exe, "not a real PE; only the path matters");
        try
        {
            Assert.True(_host.UpdateSettings(JsonSerializer.SerializeToElement(new
            {
                game = new
                {
                    gameList = new[]
                    {
                        new { id = "custom-game", name = "Custom game", executablePath = exe },
                    },
                },
            })));

            await _host.PushTraining("custom-game");
        }
        finally
        {
            File.Delete(exe);
        }
    }
#endif

    [WindowsFact]
    public void NormalizePickedExecutable_AcceptsOnlyExistingExecutables()
    {
        var exe = ExecutablePath("pick.exe");
        File.WriteAllText(exe, "pick me");
        try
        {
            Assert.Equal(exe, AppHost.NormalizePickedExecutable(exe));
            Assert.Equal(exe, AppHost.NormalizePickedExecutable($" {exe} "));
            Assert.Null(AppHost.NormalizePickedExecutable(Path.Combine(_contentRoot, "missing.exe")));
            Assert.Null(AppHost.NormalizePickedExecutable(Path.Combine(Path.GetTempPath(), "some.txt")));
            Assert.Null(AppHost.NormalizePickedExecutable("   "));
            Assert.Null(AppHost.NormalizePickedExecutable(null));
        }
        finally
        {
            File.Delete(exe);
        }
    }
}
