// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Serilog;

namespace Tript.App.Updater;

internal sealed class GitHubReleaseClient
{
    // Never /releases/latest: that endpoint excludes prereleases and 404s when a repo (like this
    // one, so far) has never cut a non-prerelease release. The list endpoint is already
    // newest-first; per_page=10 gives headroom for the defensive Draft filter below even though
    // the public API generally doesn't return drafts at all.
    private static readonly Uri DefaultReleasesUri =
        new("https://api.github.com/repos/LeSqueed/Tript/releases?per_page=10");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly Uri _releasesUri;

    internal GitHubReleaseClient(HttpClient http, Uri? releasesUri = null)
    {
        _http = http;
        _releasesUri = releasesUri ?? DefaultReleasesUri;
    }

    internal async Task<GitHubRelease?> GetLatestPublishedReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _releasesUri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Tript-Updater", "1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var releases = await response.Content
                .ReadFromJsonAsync<List<GitHubRelease>>(JsonOptions, cancellationToken).ConfigureAwait(false);
            return releases?.FirstOrDefault(release => !release.Draft);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException)
        {
            Log.Debug(exception, "GitHubReleaseClient: could not check for a new release");
            return null;
        }
    }

    // Returns null when the release doesn't ship the expected Windows asset shape (e.g. a future
    // release published for a different platform only) - treated as inapplicable, not an error.
    internal static (string Version, string ZipAssetName, string Sha256AssetName)? ParseTagAndAssetNames(
        GitHubRelease release)
    {
        var tag = release.TagName.Trim();
        if (tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V'))
            tag = tag[1..];
        if (tag.Length == 0)
            return null;

        var zipName = $"Tript-{tag}-win-x64.zip";
        var shaName = $"{zipName}.sha256";
        var hasZip = release.Assets.Any(asset => string.Equals(asset.Name, zipName, StringComparison.Ordinal));
        var hasSha = release.Assets.Any(asset => string.Equals(asset.Name, shaName, StringComparison.Ordinal));
        return hasZip && hasSha ? (tag, zipName, shaName) : null;
    }
}
