using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tript.App.Resolver;

internal sealed class ResolverClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(Wire.Options)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ResolverConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ResolverGameRegistry? _registry;

    internal ResolverClient(ResolverConfig config, HttpClient? httpClient = null,
        ResolverGameRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = httpClient is null;
        _registry = registry;
    }

    internal Uri ManifestUri => _config.Endpoint("manifest");

    internal ResolverConfig Config => _config;

    internal async Task<ResolvedGame> ResolveAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var game = await GetAsync<ResolvedGame>(
            _config.Endpoint($"resolve?input={Uri.EscapeDataString(input.Trim())}"), authenticated: true,
            cancellationToken);
        _registry?.TryUpsert(game);
        return game;
    }

    internal async Task<IReadOnlyList<ResolverSearchResult>> SearchAsync(string query, int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        limit = Math.Clamp(limit, 1, 50);
        var response = await GetAsync<ResolverSearchResponse>(
            _config.Endpoint($"search?q={Uri.EscapeDataString(query.Trim())}&limit={limit}"), authenticated: true,
            cancellationToken);
        return response.Results;
    }

    internal async Task<ResolvedGame> GetGameAsync(string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        var game = await GetAsync<ResolvedGame>(_config.Endpoint($"games/{Uri.EscapeDataString(gameId.Trim())}"),
            authenticated: false, cancellationToken);
        _registry?.TryUpsert(game);
        return game;
    }

    internal async Task<GameRequestResult> RequestGameAsync(string gameId, string installId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installId);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            _config.Endpoint($"games/{Uri.EscapeDataString(gameId.Trim())}/request"))
        {
            Content = JsonContent.Create(new { installId }, options: JsonOptions),
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<GameRequestResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The resolver returned an empty response.");
        return response.StatusCode switch
        {
            HttpStatusCode.OK when payload.Status == "already_requested" =>
                new GameRequestResult(GameRequestStatus.AlreadyRequested, null),
            HttpStatusCode.OK => new GameRequestResult(GameRequestStatus.Accepted, null),
            HttpStatusCode.TooManyRequests => new GameRequestResult(GameRequestStatus.RateLimited,
                payload.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromHours(6)),
            _ => throw new HttpRequestException($"Unexpected resolver response: {(int)response.StatusCode}"),
        };
    }

    private async Task<T> GetAsync<T>(Uri uri, bool authenticated, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (authenticated && _config.ApiKey is not null)
            request.Headers.Add("X-Api-Key", _config.ApiKey);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The resolver returned an empty response.");
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

internal sealed class ResolvedGame
{
    public string GameId { get; init; } = string.Empty;
    public bool Canonical { get; init; }
    public string Source { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int? Year { get; init; }
    public string? Platforms { get; init; }
    public IReadOnlyList<ResolverAlias>? Aliases { get; init; }
}

internal sealed class ResolverAlias
{
    public string GameId { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Provenance { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
    public DateTimeOffset VerifiedAt { get; init; }
}

internal sealed class ResolverSearchResult
{
    public string? GameId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int? Year { get; init; }
    public string? Platforms { get; init; }
    public string Source { get; init; } = string.Empty;
    public long? SteamAppId { get; init; }
    public long? IgdbId { get; init; }
}

internal sealed class ResolverSearchResponse
{
    public IReadOnlyList<ResolverSearchResult> Results { get; init; } = [];
}

internal enum GameRequestStatus { Accepted, AlreadyRequested, RateLimited }

internal readonly record struct GameRequestResult(GameRequestStatus Status, TimeSpan? RetryAfter);

internal sealed class GameRequestResponse
{
    public string Status { get; init; } = string.Empty;
    public int? RetryAfterSeconds { get; init; }
    public string? Error { get; init; }
}
