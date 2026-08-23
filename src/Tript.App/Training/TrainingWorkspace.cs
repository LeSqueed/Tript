// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Tript.Settings;
using System.Text.Json;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed class TrainingWorkspace
{
    private TrainingWorkspace(string gameId, string rootPath)
    {
        GameId = gameId;
        RootPath = rootPath;
    }

    internal string GameId { get; }

    internal string RootPath { get; }

    internal string EventsPath => Path.Combine(RootPath, "events.json");

    internal string ModelPath => Path.Combine(RootPath, "model.onnx");

    internal string SamplesPath => Path.Combine(RootPath, "samples");

    internal string DatasetPath => Path.Combine(RootPath, "dataset");

    internal string RunsPath => Path.Combine(RootPath, "runs");

    internal List<EventDefinition> LoadDefinitions()
    {
        if (!File.Exists(EventsPath))
            return [];

        return JsonSerializer.Deserialize<List<EventDefinition>>(File.ReadAllText(EventsPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? [];
    }

    // Runtime event definitions are installed beside the model. The training workspace keeps its
    // own copy for dataset/training operations, but the detector and training UI must agree on this
    // one authoritative definition file.
    internal List<EventDefinition> LoadRuntimeDefinitions()
    {
        var runtimePath = TrainingPaths.InstalledEventsPath(GameId);
        if (!File.Exists(runtimePath))
            return LoadDefinitions();

        return JsonSerializer.Deserialize<List<EventDefinition>>(File.ReadAllText(runtimePath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? [];
    }

    internal static TrainingWorkspace ForGame(string gameId, string? rootPath = null)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("A training workspace requires a game id.", nameof(gameId));

        var segment = SafeGameSegment(gameId);
        var root = rootPath ?? TrainingPaths.RootPath;
        return new TrainingWorkspace(gameId, Path.Combine(root, segment));
    }

    internal static TrainingWorkspace AtRoot(string gameId, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("A training workspace requires a game id.", nameof(gameId));

        _ = SafeGameSegment(gameId);
        return new TrainingWorkspace(gameId, rootPath);
    }

    internal void EnsureDirectories()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(SamplesPath);
        Directory.CreateDirectory(DatasetPath);
        Directory.CreateDirectory(RunsPath);
    }

    internal string Revision()
    {
        if (!Directory.Exists(RootPath))
            return string.Empty;

        return string.Join("|", Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{Path.GetRelativePath(RootPath, path)}:{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}"));
    }

    private static string SafeGameSegment(string gameId)
    {
        if (gameId is "." or ".." || gameId.Contains(Path.DirectorySeparatorChar)
            || gameId.Contains(Path.AltDirectorySeparatorChar)
            || gameId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The game id cannot be used as a directory name.", nameof(gameId));
        }

        return gameId;
    }
}

internal static class TrainingPaths
{
    internal static string RootPath => Path.Combine(SettingsFilePaths.ConfigDirectory, "training");

    internal static string InstalledModelsPath => Path.Combine(SettingsFilePaths.ConfigDirectory, "models");

    internal static string InstalledModelPath(string gameId) =>
        Path.Combine(TrainingWorkspace.ForGame(gameId, InstalledModelsPath).RootPath, "model.onnx");

    internal static string InstalledEventsPath(string gameId) =>
        Path.Combine(TrainingWorkspace.ForGame(gameId, InstalledModelsPath).RootPath, "events.json");
}

#endif
