// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace Tript.App.Models;

internal sealed class GameModelManifestSource
{
    internal const int SupportedVersion = 1;

    private const int MaximumBytes = 1024 * 1024;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private static readonly Uri DefaultUri = new(
        "https://raw.githubusercontent.com/LeSqueed/Tript/main/data/model-manifest.json");

    private readonly string _manifestPath;
    private readonly string _statePath;
    private readonly string? _bundledPath;
    private readonly Uri _uri;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _utcNow;

    internal GameModelManifestSource(string manifestPath, string statePath, string? bundledPath, Uri? uri,
        HttpClient http, Func<DateTimeOffset> utcNow)
    {
        _manifestPath = manifestPath;
        _statePath = statePath;
        _bundledPath = bundledPath;
        _uri = uri ?? DefaultUri;
        _http = http;
        _utcNow = utcNow;
    }

    internal async Task<GameModelManifest?> GetAsync(CancellationToken cancellationToken)
    {
        var cached = ModelJsonFiles.Read<GameModelManifest>(_manifestPath) ?? ModelJsonFiles.Read<GameModelManifest>(_bundledPath);
        var cacheState = ModelJsonFiles.Read<GameModelManifestCache>(_statePath);
        if (cached is not null && cacheState is not null &&
            _utcNow() - cacheState.CheckedAt < CheckInterval)
        {
            return Validate(cached);
        }
        if (cached is not null && cacheState is not null &&
            _utcNow() - cacheState.LastAttemptAt < RetryInterval)
        {
            return Validate(cached);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
            if (!string.IsNullOrWhiteSpace(cacheState?.ETag) &&
                EntityTagHeaderValue.TryParse(cacheState.ETag, out var etag))
            {
                request.Headers.IfNoneMatch.Add(etag);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                ModelJsonFiles.WriteAtomic(_statePath, new GameModelManifestCache
                {
                    CheckedAt = _utcNow(),
                    LastAttemptAt = _utcNow(),
                    ETag = cacheState?.ETag,
                });
                return Validate(cached);
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumBytes)
                throw new InvalidDataException("The model manifest exceeded its allowed size.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var manifestBytes = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (manifestBytes.Length + read > MaximumBytes)
                    throw new InvalidDataException("The model manifest exceeded its allowed size.");
                manifestBytes.Write(buffer, 0, read);
            }
            var downloaded = JsonSerializer.Deserialize<GameModelManifest>(manifestBytes.ToArray(), ModelJsonFiles.Options)
                ?? throw new InvalidDataException("The model manifest was empty.");
            Validate(downloaded);
            ModelJsonFiles.WriteAtomic(_manifestPath, downloaded);
            ModelJsonFiles.WriteAtomic(_statePath, new GameModelManifestCache
            {
                CheckedAt = _utcNow(),
                LastAttemptAt = _utcNow(),
                ETag = response.Headers.ETag?.ToString(),
            });
            return downloaded;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            if (cached is not null)
            {
                Log.Debug(exception, "GameModelManager: using cached model manifest");
                ModelJsonFiles.WriteAtomic(_statePath, new GameModelManifestCache
                {
                    CheckedAt = cacheState?.CheckedAt ?? default,
                    LastAttemptAt = _utcNow(),
                    ETag = cacheState?.ETag,
                });
                return Validate(cached);
            }
            throw;
        }
    }

    internal static GameModelManifest Validate(GameModelManifest manifest)
    {
        if (manifest.SchemaVersion != SupportedVersion)
            throw new InvalidDataException($"Unsupported model manifest version {manifest.SchemaVersion}.");
        if (manifest.Games.GroupBy(game => game.GameId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("The model manifest contains duplicate game IDs.");
        foreach (var game in manifest.Games)
        {
            GameModelPaths.ValidateGameId(game.GameId);
            if (game.Releases.GroupBy(release => (release.ModelApiVersion, release.Revision)).Any(group => group.Count() > 1))
                throw new InvalidDataException($"The model manifest contains duplicate releases for {game.GameId}.");
        }
        return manifest;
    }
}
