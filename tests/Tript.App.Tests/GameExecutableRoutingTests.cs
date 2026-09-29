// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class GameExecutableRoutingTests : IDisposable
{
    private const string OverwatchId = "5JWDDE307Z5127JK7KM4YCB1XW";

    private readonly string _contentRoot;
    private readonly SettingsStore _store;
    private readonly AppHost _host;

    public GameExecutableRoutingTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(GameExecutableRoutingTests),
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
        }, _store, runtime: null, new RecordingSessionTracker(),
            storageProbe: AmpleStorage.Probe);
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

    private void Catalogue(params GameSetting[] games)
    {
        var list = _store.Load().Game.GameList;
        list.Clear();
        list.AddRange(games);
        _host.ReloadGameList();
    }

    [Fact]
    public void AResolvedGameCarriesTheExecutableOfItsPath()
    {
        Catalogue(new GameSetting
        {
            Id = OverwatchId,
            Name = "Overwatch",
            ExecutablePath = Path.Combine(_contentRoot, "Overwatch", "Overwatch.exe"),
        });

        Assert.Equal("Overwatch.exe", Assert.Single(_host.GameList).Executable);
    }

    [Fact]
    public void APathlessResolvedGame_IsTargetedByItsExecutableName()
    {
        Catalogue(new GameSetting { Id = OverwatchId, Name = "Overwatch" });

        var target = Assert.Single(_host.BuildDetectionTargets());
        Assert.Equal(OverwatchId, target.GameId);
        Assert.Equal("Overwatch", target.Executable);
        Assert.Null(target.ExecutablePath);
    }

    [Fact]
    public void APathlessCustomGame_IsNotTargeted()
    {
        Catalogue(new GameSetting { Id = "custom-doom", Name = "Doom" });

        Assert.Empty(_host.BuildDetectionTargets());
    }

    [Fact]
    public void AGameMatchedByName_RemembersTheDetectedPath()
    {
        var executable = Path.Combine(_contentRoot, "Overwatch", "Overwatch.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "exe");
        Catalogue(new GameSetting { Id = OverwatchId, Name = "Overwatch", AutoRecordOverride = false });

        _host.RememberDetectedExecutablePath(OverwatchId, executable);

        var saved = Assert.Single(_store.Load().Game.GameList);
        Assert.Equal(executable, saved.ExecutablePath);
        Assert.False(saved.AutoRecordOverride);
        Assert.Equal(executable, Assert.Single(_host.BuildDetectionTargets()).ExecutablePath);
    }

    [Fact]
    public void AGameWithAPath_DoesNotTakeOnAnotherDetectedPath()
    {
        var existing = Path.Combine(_contentRoot, "A", "Overwatch.exe");
        var other = Path.Combine(_contentRoot, "B", "Overwatch.exe");
        foreach (var path in new[] { existing, other })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "exe");
        }
        Catalogue(new GameSetting { Id = OverwatchId, Name = "Overwatch", ExecutablePath = existing });

        _host.RememberDetectedExecutablePath(OverwatchId, other);

        Assert.Equal(existing, Assert.Single(_store.Load().Game.GameList).ExecutablePath);
    }

    [Fact]
    public void OnlyTheGamesInSettingsAreListed()
    {
        Catalogue(new GameSetting { Id = "cs2", Name = "Counter-Strike 2", Executable = "cs2.exe" });

        var cs2 = Assert.Single(_host.GameList);
        Assert.Equal("Counter-Strike 2", cs2.Name);
        Assert.Equal("cs2.exe", cs2.Executable);
    }

    [Fact]
    public void TheGameListPush_SpellsItExecutable()
    {
        Catalogue(new GameSetting { Id = "cs2", Name = "Counter-Strike 2", Executable = "cs2.exe" });

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(_host.GameList, Wire.Options));

        var entry = document.RootElement[0];
        Assert.Equal("Counter-Strike 2", entry.GetProperty("name").GetString());
        Assert.Equal("cs2.exe", entry.GetProperty("executable").GetString());
    }

    [Fact]
    public void GameCapture_HooksTheExecutable_NotTheDisplayName()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("Overwatch", _host.GameCaptureName("Overwatch"));
    }

    [Fact]
    public void GameCapture_FallsBackToTheDisplayName_WhenNoExecutableIsSet()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("Overwatch", _host.GameCaptureName("Overwatch"));
    }

    [Fact]
    public void GameCapture_ForAnUnlistedGame_UsesTheIdItself()
    {
        Catalogue(new GameSetting { Id = "Overwatch", Name = "Overwatch" });

        Assert.Equal("doom", _host.GameCaptureName("doom"));
    }
}
