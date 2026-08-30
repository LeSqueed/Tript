// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

// The custom-game contract: exact-path matching, atomic validation of the whole game list, and the
// add-from-suggestion path that turns a fullscreen candidate into a persisted custom game. The
// packaged catalogue stays immutable throughout.
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

    [Fact]
    public void IgnoreGameCandidate_AndMissingExactPaths_DoNotBreakDetectionTargets()
    {
        // Unknown fullscreen candidates are suggestions only: they must never create detection
        // targets that would start a recording.
        _host.IgnoreGameCandidate(@"C:\Tools\some.bin");
        Assert.DoesNotContain(_host.GameList, game => game.Id.StartsWith("custom-"));
    }

    [Fact]
    public void NormalizePickedExecutable_AcceptsOnlyExistingExecutables()
    {
        if (!OperatingSystem.IsWindows())
            return;

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
