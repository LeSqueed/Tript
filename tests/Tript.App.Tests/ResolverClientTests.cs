using System.Net;
using System.Text;
using Tript.App.Resolver;
using Xunit;

namespace Tript.App.Tests;

public sealed class ResolverClientTests
{
    [Fact]
    public void Config_UsesDeploymentFileAndNormalizesValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-resolver-config", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, ResolverConfig.FileName);
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(ResolverConfig.FromFile(path));
            File.WriteAllText(path, """{"url":"https://resolver.test/api","apiKey":" secret "}""");

            var configured = ResolverConfig.FromFile(path);

            Assert.Equal("https://resolver.test/api/", configured!.BaseUri.AbsoluteUri);
            Assert.Equal("secret", configured.ApiKey);
            Assert.Equal("https://resolver.test/api/manifest", configured.Endpoint("manifest").AbsoluteUri);

            File.WriteAllText(path, """{"url":"http://127.0.0.1:18895","apiKey":null}""");
            Assert.Null(ResolverConfig.FromFile(path)!.ApiKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Config_RejectsNonHttpUrls()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"url":"file:///tmp/resolver","apiKey":null}""");
            Assert.Throws<InvalidOperationException>(() => ResolverConfig.FromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Client_UsesTheResolverContracts()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-resolver-tests", Guid.NewGuid().ToString("N"));
        var registryPath = Path.Combine(root, "resolver-games.json");
        var handler = new ResolverHandler();
        using var http = new HttpClient(handler);
        var registry = new ResolverGameRegistry(registryPath);
        using var client = new ResolverClient(new ResolverConfig(new Uri("https://resolver.test/"), "key"), http,
            registry);

        try
        {
            var resolved = await client.ResolveAsync("name:Red Dead Redemption 2");
            var results = await client.SearchAsync("Red Dead", 100);
            var game = await client.GetGameAsync(resolved.GameId);

            Assert.Equal("01HRESOLVEDGAME000000000000", resolved.GameId);
            Assert.Equal("Red Dead Redemption 2", resolved.DisplayName);
            var result = Assert.Single(results);
            Assert.Equal(resolved.GameId, result.GameId);
            Assert.Equal(456, result.IgdbId);
            Assert.Equal(resolved.GameId, game.GameId);
            Assert.Equal("https://resolver.test/manifest", client.ManifestUri.AbsoluteUri);
            Assert.Equal("/resolve?input=name%3ARed%20Dead%20Redemption%202", handler.Requests[0].PathAndQuery);
            Assert.Equal("/search?q=Red%20Dead&limit=50", handler.Requests[1].PathAndQuery);
            Assert.Equal("/games/01HRESOLVEDGAME000000000000", handler.Requests[2].PathAndQuery);
            Assert.Equal("key", handler.Requests[0].ApiKey);
            Assert.Equal("key", handler.Requests[1].ApiKey);
            Assert.Null(handler.Requests[2].ApiKey);
            Assert.True(new ResolverGameRegistry(registryPath).TryGet(resolved.GameId, out var cached));
            Assert.Equal(resolved.DisplayName, cached!.DisplayName);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ResolverHandler : HttpMessageHandler
    {
        internal List<(string PathAndQuery, string? ApiKey)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((path, request.Headers.TryGetValues("X-Api-Key", out var values)
                ? values.Single()
                : null));
            var json = path.StartsWith("/search", StringComparison.Ordinal)
                ? """{"results":[{"gameId":"01HRESOLVEDGAME000000000000","name":"Red Dead Redemption 2","source":"igdb","igdbId":456}]}"""
                : """{"gameId":"01HRESOLVEDGAME000000000000","canonical":true,"source":"store","displayName":"Red Dead Redemption 2"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
