// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Tript.Settings;
using System.Text.Json;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed class TrainingPreferences
{
    public int Epochs { get; init; } = 100;
    public string Device { get; init; } = "auto";
    public int AugmentCopies { get; init; }
    public int OcrEpochs { get; init; } = 50;
}

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

    internal string RegionGroupsPath => Path.Combine(RootPath, "regionGroups.json");

    internal string ModelPath => Path.Combine(RootPath, "model.onnx");

    internal string OcrDetectorPath => Path.Combine(RootPath, "ocr_detector.onnx");

    internal string OcrModelPath => Path.Combine(RootPath, "ocr_model.onnx");

    internal string OcrDictionaryPath => Path.Combine(RootPath, "ocr_dict.txt");

    internal string PreferencesPath => Path.Combine(RootPath, "preferences.json");

    internal string SamplesPath => Path.Combine(RootPath, "samples");

    internal string DatasetPath => Path.Combine(RootPath, "dataset");

    internal string RunsPath => Path.Combine(RootPath, "runs");

    internal string TrainingProgressPath => Path.Combine(DatasetPath, "progress.json");

    internal List<EventDefinition> LoadDefinitions()
    {
        if (!File.Exists(EventsPath))
            return [];

        return JsonSerializer.Deserialize<List<EventDefinition>>(File.ReadAllText(EventsPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? [];
    }

    internal List<TrainingRegionGroup> LoadRegionGroups()
    {
        if (!File.Exists(RegionGroupsPath))
            return [];

        return JsonSerializer.Deserialize<List<TrainingRegionGroup>>(
            File.ReadAllText(RegionGroupsPath), TrainingRegionResolver.JsonOptions) ?? [];
    }

    internal void SaveRegionGroups(IReadOnlyList<TrainingRegionGroup> groups)
    {
        EnsureDirectories();
        TrainingSampleStore.WriteAtomically(RegionGroupsPath,
            JsonSerializer.SerializeToUtf8Bytes(groups, TrainingRegionResolver.WriteJsonOptions));
    }

    internal TrainingPreferences? LoadPreferences()
    {
        if (!File.Exists(PreferencesPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TrainingPreferences>(File.ReadAllText(PreferencesPath),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal void SavePreferences(TrainingPreferences preferences)
    {
        EnsureDirectories();
        TrainingSampleStore.WriteAtomically(PreferencesPath, JsonSerializer.SerializeToUtf8Bytes(
            preferences, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
    }

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
        => BuildRevision(IsRevisionFile);

    internal string SourceRevision()
        => BuildRevision(path =>
            string.Equals(path, EventsPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, RegionGroupsPath, StringComparison.OrdinalIgnoreCase)
            || Path.GetRelativePath(RootPath, path).StartsWith(
                "samples" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private string BuildRevision(Func<string, bool> include)
    {
        if (!Directory.Exists(RootPath))
            return string.Empty;

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception exception) when (IsTransientFileSystemError(exception))
        {
            return string.Empty;
        }

        var revisions = new List<string>();
        foreach (var path in paths.Where(include).OrderBy(path => path,
                     StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(path);
                revisions.Add($"{Path.GetRelativePath(RootPath, path)}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception exception) when (IsTransientFileSystemError(exception))
            {
            }
        }
        return string.Join("|", revisions);
    }

    private bool IsRevisionFile(string path)
    {
        if (string.Equals(path, PreferencesPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, TrainingProgressPath, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains(".tmp-", StringComparison.OrdinalIgnoreCase))
            return false;

        return !Path.GetRelativePath(RootPath, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.StartsWith("dataset.previous-", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsTransientFileSystemError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

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
