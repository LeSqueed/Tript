// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Text;
using System.Text.Json;
using Tript.App.Updater;
using Xunit;

namespace Tript.App.Tests;

public sealed class GitHubReleaseClientTests
{
    private static readonly Uri ReleasesUri = new("https://api.test/repos/LeSqueed/Tript/releases");

    [Fact]
    public async Task GetLatestPublishedReleaseAsync_UsesTheListEndpointNotLatest()
    {
        var handler = new RouteHandler(ReleasesUri.ToString(), Releases(
            new
            {
                tag_name = "v0.1.0-alpha.3", draft = false, prerelease = true,
                html_url = "https://github.com/LeSqueed/Tript/releases/tag/v0.1.0-alpha.3",
                assets = Array.Empty<object>(),
            }));
        var client = new GitHubReleaseClient(new HttpClient(handler), ReleasesUri);

        var release = await client.GetLatestPublishedReleaseAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("v0.1.0-alpha.3", release!.TagName);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("/latest", handler.Requests[0]);
    }

    [Fact]
    public async Task GetLatestPublishedReleaseAsync_SkipsDraftsAndReturnsTheFirstNonDraft()
    {
        var handler = new RouteHandler(ReleasesUri.ToString(), Releases(
            new
            {
                tag_name = "v0.2.0-alpha.1", draft = true, prerelease = true, html_url = "https://x",
                assets = Array.Empty<object>(),
            },
            new
            {
                tag_name = "v0.1.0-alpha.3", draft = false, prerelease = true, html_url = "https://x",
                assets = Array.Empty<object>(),
            }));
        var client = new GitHubReleaseClient(new HttpClient(handler), ReleasesUri);

        var release = await client.GetLatestPublishedReleaseAsync(CancellationToken.None);

        Assert.Equal("v0.1.0-alpha.3", release!.TagName);
    }

    [Fact]
    public async Task GetLatestPublishedReleaseAsync_ReturnsNullOnNetworkFailure()
    {
        var handler = new RouteHandler(ReleasesUri.ToString(), HttpStatusCode.InternalServerError);
        var client = new GitHubReleaseClient(new HttpClient(handler), ReleasesUri);

        Assert.Null(await client.GetLatestPublishedReleaseAsync(CancellationToken.None));
    }

    [Fact]
    public void ParseTagAndAssetNames_ResolvesTheExpectedWindowsAssetPair()
    {
        var release = new GitHubRelease
        {
            TagName = "v0.1.0-alpha.3",
            Assets =
            [
                new GitHubReleaseAsset { Name = "Tript-0.1.0-alpha.3-win-x64.zip" },
                new GitHubReleaseAsset { Name = "Tript-0.1.0-alpha.3-win-x64.zip.sha256" },
            ],
        };

        var result = GitHubReleaseClient.ParseTagAndAssetNames(release);

        Assert.NotNull(result);
        Assert.Equal("0.1.0-alpha.3", result!.Value.Version);
        Assert.Equal("Tript-0.1.0-alpha.3-win-x64.zip", result.Value.ZipAssetName);
        Assert.Equal("Tript-0.1.0-alpha.3-win-x64.zip.sha256", result.Value.Sha256AssetName);
    }

    [Fact]
    public void ParseTagAndAssetNames_ReturnsNullWhenTheShaSidecarIsMissing()
    {
        var release = new GitHubRelease
        {
            TagName = "v0.1.0-alpha.3",
            Assets = [new GitHubReleaseAsset { Name = "Tript-0.1.0-alpha.3-win-x64.zip" }],
        };

        Assert.Null(GitHubReleaseClient.ParseTagAndAssetNames(release));
    }

    private static byte[] Releases(params object[] releases) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(releases));

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly string _url;
        private readonly byte[]? _content;
        private readonly HttpStatusCode _statusCode;

        internal RouteHandler(string url, byte[] content)
        {
            _url = url;
            _content = content;
            _statusCode = HttpStatusCode.OK;
        }

        internal RouteHandler(string url, HttpStatusCode statusCode)
        {
            _url = url;
            _statusCode = statusCode;
        }

        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri!.AbsoluteUri != _url || _content is null)
                return Task.FromResult(new HttpResponseMessage(_content is null ? _statusCode : HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new ByteArrayContent(_content),
            });
        }
    }
}
