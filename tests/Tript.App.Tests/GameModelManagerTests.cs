// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tript.App.Models;
using Xunit;

namespace Tript.App.Tests;

public sealed class GameModelManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tript-model-manager-" + Guid.NewGuid().ToString("N"));

    public GameModelManagerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CompatibleRelease_IsVerifiedAndInstalledAtomically()
    {
        var package = BuildOverwatchPackage(revision: 2);
        var packageHash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var manifest = Manifest("Overwatch", apiVersion: 1, revision: 2,
            package.Length, packageHash, "http://127.0.0.1:8895/overwatch.zip");
        var handler = new RouteHandler(new Dictionary<string, byte[]>
        {
            ["https://models.test/manifest.json"] = Encoding.UTF8.GetBytes(manifest),
            ["http://127.0.0.1:8895/overwatch.zip"] = package,
        });
        var statuses = new List<IReadOnlyList<GameModelStatus>>();
        using var manager = CreateManager(handler, async (gameId, stagedPath, _) =>
        {
            GameModelInstaller.InstallValidatedDirectory(gameId, stagedPath, ModelsRoot);
            await Task.CompletedTask;
        });
        manager.StatusChanged += snapshot => statuses.Add(snapshot);

        await manager.EnsureModelAsync("Overwatch");

        Assert.True(File.Exists(Path.Combine(ModelsRoot, "Overwatch", "model.onnx")));
        Assert.True(File.Exists(Path.Combine(ModelsRoot, "Overwatch", "events.json")));
        var installed = JsonSerializer.Deserialize<InstalledGameModel>(
            File.ReadAllText(Path.Combine(ModelsRoot, "Overwatch", "installed.json")), Wire.Options);
        Assert.Equal(2, installed?.Revision);
        Assert.Contains(statuses.SelectMany(snapshot => snapshot), status => status.Stage == "downloading");
        Assert.Equal("ready", Assert.Single(manager.Snapshot()).Stage);
    }

    [Fact]
    public async Task IncompatibleRelease_IsNotDownloaded()
    {
        var manifest = Manifest("FutureGame", apiVersion: 2, revision: 1,
            size: 100, new string('a', 64), "https://models.test/future.zip");
        var handler = new RouteHandler(new Dictionary<string, byte[]>
        {
            ["https://models.test/manifest.json"] = Encoding.UTF8.GetBytes(manifest),
        });
        using var manager = CreateManager(handler, (_, _, _) => Task.CompletedTask);

        await manager.EnsureModelAsync("FutureGame");

        var status = Assert.Single(manager.Snapshot());
        Assert.Equal("unsupported", status.Stage);
        Assert.DoesNotContain("https://models.test/future.zip", handler.Requests);
    }

    [Fact]
    public async Task FreshCachedManifest_AvoidsAnotherNetworkCheck()
    {
        var manifestPath = Path.Combine(_root, "manifest.json");
        var statePath = Path.Combine(_root, "manifest-state.json");
        File.WriteAllText(manifestPath, "{\"schemaVersion\":1,\"games\":[]}");
        File.WriteAllText(statePath,
            "{\"checkedAt\":\"2026-08-26T12:00:00+00:00\"}");
        var handler = new RouteHandler(new Dictionary<string, byte[]>());
        using var manager = new GameModelManager((_, _, _) => Task.CompletedTask,
            modelsRoot: ModelsRoot, manifestPath: manifestPath, manifestStatePath: statePath,
            manifestUri: new Uri("https://models.test/manifest.json"),
            httpClient: new HttpClient(handler), utcNow: () => new DateTimeOffset(2026, 8, 26, 13, 0, 0, TimeSpan.Zero));

        await manager.EnsureModelAsync("Overwatch");

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PackageHashMismatch_LeavesNoInstalledModel()
    {
        var package = new byte[] { 1, 2, 3 };
        var manifest = Manifest("BrokenGame", apiVersion: 1, revision: 1,
            package.Length, new string('0', 64), "https://models.test/broken.zip");
        var handler = new RouteHandler(new Dictionary<string, byte[]>
        {
            ["https://models.test/manifest.json"] = Encoding.UTF8.GetBytes(manifest),
            ["https://models.test/broken.zip"] = package,
        });
        using var manager = CreateManager(handler, (_, _, _) => Task.CompletedTask);

        await manager.EnsureModelAsync("BrokenGame");

        Assert.Equal("error", Assert.Single(manager.Snapshot()).Stage);
        Assert.False(Directory.Exists(Path.Combine(ModelsRoot, "BrokenGame")));
    }

    [Fact]
    public async Task StaleManifestFailure_RetriesAfterShortBackoff()
    {
        var manifestPath = Path.Combine(_root, "manifest.json");
        var statePath = Path.Combine(_root, "manifest-state.json");
        File.WriteAllText(manifestPath, "{\"schemaVersion\":1,\"games\":[]}");
        File.WriteAllText(statePath,
            "{\"checkedAt\":\"2026-08-25T11:00:00+00:00\"}");
        var handler = new RouteHandler(new Dictionary<string, byte[]>());

        await EnsureAt(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        Assert.Single(handler.Requests);
        await EnsureAt(new DateTimeOffset(2026, 8, 26, 12, 10, 0, TimeSpan.Zero));
        Assert.Single(handler.Requests);
        await EnsureAt(new DateTimeOffset(2026, 8, 26, 12, 16, 0, TimeSpan.Zero));
        Assert.Equal(2, handler.Requests.Count);

        async Task EnsureAt(DateTimeOffset now)
        {
            using var manager = new GameModelManager((_, _, _) => Task.CompletedTask,
                modelsRoot: ModelsRoot, manifestPath: manifestPath, manifestStatePath: statePath,
                manifestUri: new Uri("https://models.test/manifest.json"),
                httpClient: new HttpClient(handler), utcNow: () => now);
            await manager.EnsureModelAsync("Overwatch");
        }
    }

    [Fact]
    public void FailedDirectorySwap_RestoresPreviousModel()
    {
        var target = Path.Combine(ModelsRoot, "SwapGame");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "previous");

        Assert.ThrowsAny<IOException>(() => GameModelInstaller.InstallValidatedDirectory(
            "SwapGame", Path.Combine(ModelsRoot, "missing-stage"), ModelsRoot));

        Assert.Equal("previous", File.ReadAllText(Path.Combine(target, "marker.txt")));
        Assert.Empty(Directory.EnumerateDirectories(ModelsRoot, "SwapGame.backup-*"));
    }

    [Fact]
    public void StartupCleanup_DoesNotTouchAnotherManagersActiveDirectory()
    {
        Directory.CreateDirectory(ModelsRoot);
        using var activeLock = GameModelInstaller.TryAcquireRootLock(ModelsRoot);
        Assert.NotNull(activeLock);
        var activeDirectory = Path.Combine(ModelsRoot, ".download-active");
        Directory.CreateDirectory(activeDirectory);

        using var manager = new GameModelManager((_, _, _) => Task.CompletedTask,
            modelsRoot: ModelsRoot, manifestPath: Path.Combine(_root, "manifest.json"),
            manifestStatePath: Path.Combine(_root, "state.json"),
            manifestUri: new Uri("https://models.test/manifest.json"),
            httpClient: new HttpClient(new RouteHandler(new Dictionary<string, byte[]>())));

        Assert.True(Directory.Exists(activeDirectory));
    }

    private string ModelsRoot => Path.Combine(_root, "models");

    private GameModelManager CreateManager(HttpMessageHandler handler,
        Func<string, string, CancellationToken, Task> activate) =>
        new(activate, modelsRoot: ModelsRoot,
            manifestPath: Path.Combine(_root, "manifest.json"),
            manifestStatePath: Path.Combine(_root, "manifest-state.json"),
            manifestUri: new Uri("https://models.test/manifest.json"),
            httpClient: new HttpClient(handler),
            utcNow: () => new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero),
            appVersion: new Version(1, 0));

    private static string Manifest(string gameId, int apiVersion, int revision, long size,
        string sha256, string url) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        games = new[]
        {
            new
            {
                gameId,
                releases = new[]
                {
                    new { modelApiVersion = apiVersion, revision, url, sizeBytes = size, sha256 },
                },
            },
        },
    });

    private static byte[] BuildOverwatchPackage(int revision)
    {
        var sourceRoot = Path.Combine(AppContext.BaseDirectory, "data", "models",
            "57ZZVAZ0PJK8VQGPKB728QE57C");
        var modelPath = Path.Combine(sourceRoot, "model.onnx");
        var eventsPath = Path.Combine(sourceRoot, "events.json");
        Assert.True(File.Exists(modelPath), $"Missing test model at {modelPath}");

        var model = File.ReadAllBytes(modelPath);
        var events = File.ReadAllBytes(eventsPath);
        var package = JsonSerializer.SerializeToUtf8Bytes(new
        {
            packageFormatVersion = 1,
            gameId = "Overwatch",
            modelApiVersion = 1,
            revision,
            files = new Dictionary<string, object>
            {
                ["model.onnx"] = FileFacts(model),
                ["events.json"] = FileFacts(events),
            },
        });

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "model.onnx", model);
            AddEntry(archive, "events.json", events);
            AddEntry(archive, "package.json", package);
        }
        return output.ToArray();
    }

    private static object FileFacts(byte[] content) => new
    {
        sizeBytes = content.LongLength,
        sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
    };

    private static void AddEntry(ZipArchive archive, string name, byte[] content)
    {
        using var output = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
        output.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class RouteHandler(IReadOnlyDictionary<string, byte[]> routes) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (!routes.TryGetValue(url, out var content))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }
}
