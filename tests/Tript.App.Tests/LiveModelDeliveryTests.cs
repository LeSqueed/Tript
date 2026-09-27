// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.App.Models;
using Xunit;

namespace Tript.App.Tests;

public sealed class LiveModelDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tript-live-model-delivery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [SkippableTheory]
    [InlineData(LegacyGameIds.Overwatch)]
    public async Task ThePublishedModel_InstallsThroughTheRealClient(string gameId)
    {
        var resolver = Environment.GetEnvironmentVariable("TRIPT_LIVE_RESOLVER");
        Skip.If(string.IsNullOrWhiteSpace(resolver), "set TRIPT_LIVE_RESOLVER to run against a live resolver");

        Directory.CreateDirectory(_root);
        if (Environment.GetEnvironmentVariable("TRIPT_LIVE_SEED_CACHE") is { Length: > 0 } seed)
        {
            File.Copy(Path.Combine(seed, "model-manifest.json"), Path.Combine(_root, "manifest.json"));
            File.Copy(Path.Combine(seed, "model-manifest-state.json"), Path.Combine(_root, "manifest-state.json"));
        }

        var modelsRoot = Path.Combine(_root, "models");
        using var manager = new GameModelManager(
            (id, stagedPath, _) =>
            {
                GameModelInstaller.InstallValidatedDirectory(id, stagedPath, modelsRoot);
                return Task.CompletedTask;
            },
            modelsRoot: modelsRoot,
            manifestPath: Path.Combine(_root, "manifest.json"),
            manifestStatePath: Path.Combine(_root, "manifest-state.json"),
            manifestUri: new Uri(new Uri(resolver!.TrimEnd('/') + "/"), "manifest"),
            appVersion: new Version(99, 0));

        await manager.EnsureModelAsync(gameId);

        var status = Assert.Single(manager.Snapshot());
        Assert.True(status.Stage == "ready", $"{status.Stage}: {status.Message}");
        var installed = JsonSerializer.Deserialize<InstalledGameModel>(
            File.ReadAllText(Path.Combine(modelsRoot, gameId, "installed.json")), Wire.Options)!;
        Assert.Equal(gameId, installed.GameId);
        Assert.Equal(status.Revision, installed.Revision);
    }
}
