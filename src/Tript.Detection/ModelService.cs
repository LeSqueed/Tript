// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Serilog;

namespace Tript.Detection;

public static class ModelService
{
    // "models" rather than "training": these are the shipped runtime assets, not a training workspace.
    public static readonly string BasePath = Path.Combine(AppContext.BaseDirectory, "data", "models");

    private static string[] _userModelRoots = [];

    // An InferenceSession is ~10 MB of native memory shared by every detector on the same game, so
    // it is refcounted rather than owned by whoever asked last. Lazy gives exactly one construction
    // (GetOrAdd's factory could race and drop one undisposed), the count exactly one disposal.
    private sealed class ModelHandle
    {
        public required Lazy<InferenceSession> Session { get; init; }
        public int RefCount { get; set; }
    }

    private static readonly Dictionary<string, ModelHandle> _models = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _modelsLock = new();
    private static readonly ConcurrentDictionary<string, List<EventDefinition>> _definitions =
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
            var path = Path.Combine(GetGamePath(id), "events.json");

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

    public static void SaveEventDefinitions(string gameId, List<EventDefinition> definitions)
    {
        var key = CanonicalGameId(gameId);
        var path = Path.Combine(GetGamePath(gameId), "events.json");
        var json = JsonSerializer.Serialize(definitions, _jsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        _definitions[key] = definitions;
        Log.Information("Saved {Count} event definitions for game {GameId}", definitions.Count, gameId);
    }

    public static bool HasModelForGame(string gameId)
    {
        var gamePath = FindGameDirectory(gameId);
        if (gamePath == null)
        {
            // Debug, not Warning: most games legitimately have no model. Listing what is on disk is
            // what turns a casing mismatch from a silent no-op into something a log can explain.
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

    // Takes a reference on the game's session. Every successful call must be paired with exactly
    // one UnloadModel; the session stays alive until the last of those calls.
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
            // Outside the lock: construction reads a 10 MB file and runs ORT's graph optimizer.
            var session = handle.Session.Value;
            Log.Debug("ONNX model for game {GameId} now has {RefCount} user(s)", key, refCount);
            return session;
        }
        catch
        {
            // Lazy caches failures forever, so the poisoned entry goes with the reference: a
            // half-written model should be retried next recording, not replayed for the process.
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

    // Releases one reference taken by LoadModel. Stopping one detector must not free native memory
    // another detector on the same game is still running inference against.
    public static void UnloadModel(string gameId)
    {
        var key = CanonicalGameId(gameId);
        InferenceSession? released = null;

        lock (_modelsLock)
        {
            if (!_models.TryGetValue(key, out var handle))
            {
                // Unbalanced release (or a detector that never got a session). Definitions are
                // still dropped so a re-read picks up an edited events.json.
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

            // Nothing to dispose when the only user never got past a failed construction.
            if (handle.Session.IsValueCreated)
                released = handle.Session.Value;
        }

        // Outside the lock: native teardown must not block another game's load.
        released?.Dispose();
        Log.Information("Unloaded ONNX model for game {GameId}", key);
    }

    // Test seam. The reference count is the whole point of the cache and is not observable from
    // the InferenceSession callers get back.
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

        // ORT defaults to one intra-op thread per physical core and spins them after every
        // Run. Disabling spin and capping threads keeps idle CPU near zero.
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 2,
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

    // Directories ship with the game's own casing ("Overwatch") but the id reaching us can be
    // spelled any way. Windows papers over the mismatch; ext4 and the Flatpak runtime do not, and a
    // File.Exists miss would silently no-op the whole ML feature.
    private static string? FindGameDirectory(string gameId)
    {
        // Guard rather than let EnumerateDirectories throw: a trimmed or misbuilt package has no
        // data/training at all, and the only production caller runs inside GameIntegrationService's
        // lock on every recording start, where an exception would break recording entirely.
        if (!ModelRoots().Any(Directory.Exists))
            return null;

        string? incompleteMatch = null;
        foreach (var root in ModelRoots())
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var directory in Directory.EnumerateDirectories(root))
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

    private static bool IsCompleteModelBundle(string directory)
        => File.Exists(Path.Combine(directory, "model.onnx"))
            && File.Exists(Path.Combine(directory, "events.json"));

    private static string[] GetAvailableGameIds()
    {
        return ModelRoots()
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ModelRoots()
    {
        foreach (var root in _userModelRoots)
            yield return root;
        yield return BasePath;
    }

    public static string GetGamePath(string gameId)
    {
        // Falls back to the literal id so SaveEventDefinitions can still create a directory for a
        // game that has none yet; only lookups of existing directories need the on-disk casing.
        return FindGameDirectory(gameId) ?? Path.Combine(BasePath, gameId);
    }

    public static string GetModelPath(string gameId)
    {
        return Path.Combine(GetGamePath(gameId), "model.onnx");
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

    // Rejects the currently selected bundle for this process and reports whether a lower-priority
    // complete bundle can take over. Used when runtime contract validation finds that a custom
    // bundle is internally inconsistent; the files remain available for training and repair.
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
        }

        released?.Dispose();
    }
}


