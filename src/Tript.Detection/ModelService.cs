// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Serilog;

namespace Tript.Detection;

public static class ModelService
{
    public static readonly string BasePath = Path.Combine(AppContext.BaseDirectory, "data", "models");

    public static readonly string SharedOcrPath = Path.Combine(AppContext.BaseDirectory, "data", "ocr");

    private static string[] _userModelRoots = [];

    private sealed class ModelHandle
    {
        public required Lazy<InferenceSession> Session { get; init; }
        public int RefCount { get; set; }
    }

    private static readonly Dictionary<string, ModelHandle> _models = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _modelsLock = new();
    private static readonly ConcurrentDictionary<string, List<EventDefinition>> _definitions =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<RegionGroupDefinition>> _regionGroups =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _rejectedBundles = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<EventDefinition> LoadEventDefinitions(string gameId)
    {
        var key = CanonicalGameId(gameId);
        return _definitions.GetOrAdd(key, id =>
        {
            var path = Path.Combine(GetDetectionGamePath(id), "events.json");

            if (!File.Exists(path))
            {
                Log.Information("No events.json found for game {GameId}", id);
                return new List<EventDefinition>();
            }

            var json = File.ReadAllText(path);
            var definitions = JsonSerializer.Deserialize<List<EventDefinition>>(json, _jsonOptions) ?? new();
            Log.Information("Loaded {Count} event definitions for game {GameId}", definitions.Count, id);
            return definitions;
        });
    }

    public static IReadOnlyList<RegionGroupDefinition> LoadRegionGroups(string gameId)
    {
        var key = CanonicalGameId(gameId);
        return _regionGroups.GetOrAdd(key, id =>
        {
            var path = Path.Combine(GetDetectionGamePath(id), "regionGroups.json");
            if (!File.Exists(path)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<RegionGroupDefinition>>(
                    File.ReadAllText(path), _jsonOptions) ?? [];
            }
            catch (JsonException)
            {
                Log.Warning("Could not parse regionGroups.json for game {GameId}", id);
                return [];
            }
        });
    }

    public static void SaveEventDefinitions(string gameId, List<EventDefinition> definitions)
    {
        var key = CanonicalGameId(gameId);
        var path = Path.Combine(GetGamePath(gameId), "events.json");
        var json = JsonSerializer.Serialize(definitions, _jsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        _definitions[key] = definitions;
        _regionGroups.TryRemove(key, out _);
        Log.Information("Saved {Count} event definitions for game {GameId}", definitions.Count, gameId);
    }

    public static bool HasModelForGame(string gameId)
    {
        var gamePath = FindGameDirectory(gameId);
        if (gamePath == null)
        {
            Log.Debug("No model directory matching {GameId} under {BasePath}; available directories: {AvailableGameIds}",
                gameId, BasePath, GetAvailableGameIds());
            return false;
        }

        if (!IsCompleteModelBundle(gamePath))
        {
            Log.Debug("Model directory {GamePath} does not contain a complete model.onnx/events.json bundle",
                gamePath);
            return false;
        }

        return true;
    }

    public static bool HasDetectionBundleForGame(string gameId)
        => FindDetectionDirectory(gameId) is not null;

    public static InferenceSession LoadModel(string gameId)
    {
        var key = CanonicalGameId(gameId);
        ModelHandle handle;
        int refCount;

        lock (_modelsLock)
        {
            if (!_models.TryGetValue(key, out var existing))
            {
                existing = new ModelHandle
                {
                    Session = new Lazy<InferenceSession>(() => CreateSession(key),
                        LazyThreadSafetyMode.ExecutionAndPublication),
                };
                _models[key] = existing;
            }

            handle = existing;
            refCount = ++handle.RefCount;
        }

        try
        {
            var session = handle.Session.Value;
            Log.Debug("ONNX model for game {GameId} now has {RefCount} user(s)", key, refCount);
            return session;
        }
        catch
        {
            lock (_modelsLock)
            {
                if (--handle.RefCount <= 0
                    && _models.TryGetValue(key, out var current) && ReferenceEquals(current, handle))
                {
                    _models.Remove(key);
                }
            }
            throw;
        }
    }

    public static void UnloadModel(string gameId)
    {
        var key = CanonicalGameId(gameId);
        InferenceSession? released = null;

        lock (_modelsLock)
        {
            if (!_models.TryGetValue(key, out var handle))
            {
                _definitions.TryRemove(key, out _);
                Log.Debug("No loaded ONNX model to unload for game {GameId}", key);
                return;
            }

            if (--handle.RefCount > 0)
            {
                Log.Debug("ONNX model for game {GameId} still has {RefCount} user(s), keeping it loaded",
                    key, handle.RefCount);
                return;
            }

            _models.Remove(key);
            _definitions.TryRemove(key, out _);

            if (handle.Session.IsValueCreated)
                released = handle.Session.Value;
        }

        released?.Dispose();
        Log.Information("Unloaded ONNX model for game {GameId}", key);
    }

    internal static int GetSessionRefCount(string gameId)
    {
        var key = CanonicalGameId(gameId);
        lock (_modelsLock)
            return _models.TryGetValue(key, out var handle) ? handle.RefCount : 0;
    }

    private static InferenceSession CreateSession(string gameId)
    {
        var modelPath = GetModelPath(gameId);

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model not found for game {gameId}", modelPath);

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AppendExecutionProvider_CPU();
        var session = new InferenceSession(modelPath, options);

        Log.Information("Loaded ONNX model for game {GameId} from {ModelPath}", gameId, modelPath);
        return session;
    }

    private static string CanonicalGameId(string gameId)
    {
        var directory = FindGameDirectory(gameId);
        return directory is null ? gameId : Path.GetFileName(directory)!;
    }

    private static string? FindGameDirectory(string gameId)
    {
        if (!ModelRoots().Any(Directory.Exists))
            return null;

        string? incompleteMatch = null;
        foreach (var root in ModelRoots())
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var directory in EnumerateDirectories(root))
            {
                if (Path.GetFileName(directory).Equals(gameId, StringComparison.OrdinalIgnoreCase))
                {
                    lock (_modelsLock)
                    {
                        if (_rejectedBundles.Contains(Path.GetFullPath(directory)))
                            continue;
                    }
                    if (IsCompleteModelBundle(directory))
                        return directory;

                    incompleteMatch ??= directory;
                }
            }
        }

        return incompleteMatch;
    }

    private static string? FindDetectionDirectory(string gameId)
    {
        foreach (var root in ModelRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in EnumerateDirectories(root))
            {
                if (!Path.GetFileName(directory).Equals(gameId, StringComparison.OrdinalIgnoreCase)) continue;
                lock (_modelsLock)
                {
                    if (_rejectedBundles.Contains(Path.GetFullPath(directory))) continue;
                }
                if (IsCompleteDetectionBundle(directory)) return directory;
            }
        }
        return null;
    }

    private static bool IsCompleteModelBundle(string directory)
        => File.Exists(Path.Combine(directory, "model.onnx"))
            && File.Exists(Path.Combine(directory, "events.json"));

    private static readonly ConcurrentDictionary<string, ((DateTime WriteTimeUtc, long Length) Stamp,
        List<EventDefinition> Definitions)> _eventsFileCache = new(StringComparer.OrdinalIgnoreCase);

    private static List<EventDefinition>? ReadEventDefinitionsFile(string eventsPath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(eventsPath);
            if (!info.Exists) return null;
        }
        catch (IOException) { return null; }

        var stamp = (info.LastWriteTimeUtc, info.Length);
        if (_eventsFileCache.TryGetValue(eventsPath, out var cached) && cached.Stamp == stamp)
            return cached.Definitions;

        try
        {
            var definitions = JsonSerializer.Deserialize<List<EventDefinition>>(
                File.ReadAllText(eventsPath), _jsonOptions) ?? [];
            _eventsFileCache[eventsPath] = (stamp, definitions);
            return definitions;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsCompleteDetectionBundle(string directory)
    {
        var definitions = ReadEventDefinitionsFile(Path.Combine(directory, "events.json"));
        if (definitions is null || definitions.Count == 0) return false;
        var needsObject = definitions.Any(definition => definition.DetectionKind == DetectionKind.Object);
        var needsOcr = definitions.Any(definition => definition.DetectionKind == DetectionKind.Ocr);
        return (!needsObject || File.Exists(Path.Combine(directory, "model.onnx")))
            && (!needsOcr || HasOcrModel(directory) || HasOcrModel(SharedOcrPath));
    }

    private static string[] GetAvailableGameIds()
    {
        return ModelRoots()
            .SelectMany(EnumerateDirectories)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string[] GetLoadableGameIds()
    {
        return ModelRoots()
            .SelectMany(EnumerateDirectories)
            .Where(IsCompleteModelBundle)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(HasModelForGame)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string[] GetLoadableDetectionGameIds()
    {
        return ModelRoots()
            .SelectMany(EnumerateDirectories)
            .Where(IsCompleteDetectionBundle)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(HasDetectionBundleForGame)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] EnumerateDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToArray() : [];
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException
            or UnauthorizedAccessException or IOException)
        {
            Log.Debug(exception, "Model root {ModelRoot} became unavailable while it was being enumerated", root);
            return [];
        }
    }

    private static IEnumerable<string> ModelRoots()
    {
        foreach (var root in _userModelRoots)
            yield return root;
        yield return BasePath;
    }

    public static string GetGamePath(string gameId)
    {
        return FindGameDirectory(gameId) ?? Path.Combine(BasePath, gameId);
    }

    public static string GetDetectionGamePath(string gameId)
        => FindDetectionDirectory(gameId) ?? FindGameDirectory(gameId) ?? Path.Combine(BasePath, gameId);

    public static string GetModelPath(string gameId)
    {
        return Path.Combine(GetDetectionGamePath(gameId), "model.onnx");
    }

    public static string GetOcrModelPath(string gameId)
        => ResolveOcrAsset(gameId, "ocr_model.onnx");

    public static string GetOcrDetectorPath(string gameId)
        => ResolveOcrAsset(gameId, "ocr_detector.onnx");

    public static string GetOcrDictionaryPath(string gameId)
        => ResolveOcrAsset(gameId, "ocr_dict.txt");

    private static bool HasOcrModel(string directory) =>
        File.Exists(Path.Combine(directory, "ocr_model.onnx"))
        && File.Exists(Path.Combine(directory, "ocr_dict.txt"));

    private static string ResolveOcrAsset(string gameId, string fileName)
    {
        var gameAsset = Path.Combine(GetDetectionGamePath(gameId), fileName);
        return File.Exists(gameAsset) ? gameAsset : Path.Combine(SharedOcrPath, fileName);
    }

    public static void ConfigureUserModelRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A user model root is required.", nameof(root));

        ConfigureModelRoots(root);
    }

    public static void ConfigureModelRoots(params string[] roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Model roots cannot be empty.", nameof(roots));

        _userModelRoots = roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (_modelsLock)
            _rejectedBundles.Clear();
    }

    internal static bool RejectCurrentBundle(string gameId, out string rejectedPath)
    {
        var directory = FindGameDirectory(gameId);
        if (directory is null)
        {
            rejectedPath = string.Empty;
            return false;
        }

        rejectedPath = Path.GetFullPath(directory);
        lock (_modelsLock)
            _rejectedBundles.Add(rejectedPath);
        _definitions.TryRemove(gameId, out _);
        return FindGameDirectory(gameId) is not null;
    }

    public static void InvalidateModel(string gameId)
    {
        var key = CanonicalGameId(gameId);
        InferenceSession? released = null;
        lock (_modelsLock)
        {
            _rejectedBundles.RemoveWhere(path =>
                string.Equals(Path.GetFileName(path), gameId, StringComparison.OrdinalIgnoreCase));
            if (_models.TryGetValue(key, out var handle))
            {
                if (handle.RefCount > 0)
                    throw new InvalidOperationException($"The model for {gameId} is still in use.");

                _models.Remove(key);
                if (handle.Session.IsValueCreated)
                    released = handle.Session.Value;
            }

            _definitions.TryRemove(key, out _);
            _regionGroups.TryRemove(key, out _);
        }

        released?.Dispose();
    }
}
