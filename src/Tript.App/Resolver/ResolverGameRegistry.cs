using System.Text.Json;
using Tript.App.Models;

namespace Tript.App.Resolver;

internal sealed class ResolverGameRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(Wire.Options)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, ResolvedGame> _games;

    internal ResolverGameRegistry(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(GameModelPaths.DataRoot, "resolver-games.json"));
        _games = new Dictionary<string, ResolvedGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in Load(_path))
        {
            if (!string.IsNullOrWhiteSpace(game.GameId))
                _games[game.GameId] = game;
        }
    }

    internal bool TryGet(string gameId, out ResolvedGame? game)
    {
        lock (_gate)
            return _games.TryGetValue(gameId, out game);
    }

    internal IReadOnlyList<ResolvedGame> Snapshot()
    {
        lock (_gate)
            return _games.Values.OrderBy(game => game.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal bool TryUpsert(ResolvedGame game)
    {
        if (string.IsNullOrWhiteSpace(game.GameId))
            return false;

        lock (_gate)
        {
            _games[game.GameId] = game;
            try
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                var temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new RegistryFile
                {
                    SchemaVersion = 1,
                    Games = _games.Values.OrderBy(value => value.GameId, StringComparer.OrdinalIgnoreCase).ToArray(),
                }, JsonOptions));
                File.Move(temporaryPath, _path, true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static IReadOnlyList<ResolvedGame> Load(string path)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<RegistryFile>(File.ReadAllText(path), JsonOptions)?.Games ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private sealed class RegistryFile
    {
        public int SchemaVersion { get; init; }
        public IReadOnlyList<ResolvedGame> Games { get; init; } = [];
    }
}
