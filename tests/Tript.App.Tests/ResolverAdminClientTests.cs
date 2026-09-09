#if TRIPT_TRAINING

using System.Net;
using System.Text;
using Tript.App.Resolver;
using Xunit;

namespace Tript.App.Tests;

public sealed class ResolverAdminClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tript-admin-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Publish_LogsInAndUploadsAnAuthenticatedModelPackage()
    {
        Directory.CreateDirectory(_root);
        var model = Path.Combine(_root, "model.onnx");
        var events = Path.Combine(_root, "events.json");
        File.WriteAllBytes(model, [1, 2, 3, 4]);
        File.WriteAllText(events, "[]");
        var handler = new AdminHandler();
        using var http = new HttpClient(handler);
        using var client = new ResolverAdminClient(
            new ResolverConfig(new Uri("https://resolver.test/"), null), http);

        var revision = await client.PublishAsync("01HRESOLVEDGAME000000000000", _root, events,
            "admin", "password-123");

        Assert.Equal(1, revision);
        Assert.Equal(["/admin/login", "/admin/models?gameId=01HRESOLVEDGAME000000000000", "/admin/models"],
            handler.Paths);
        Assert.Equal([null, "Bearer token-1", "Bearer token-1"], handler.Authorization);
        Assert.Contains("name=gameId", handler.UploadBody);
        Assert.Contains("name=file", handler.UploadBody);
        Assert.Contains("filename=model.zip", handler.UploadBody);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class AdminHandler : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal List<string?> Authorization { get; } = [];
        internal string UploadBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Paths.Add(path);
            Authorization.Add(request.Headers.Authorization?.ToString());
            if (path == "/admin/models" && request.Method == HttpMethod.Post)
                UploadBody = Encoding.Latin1.GetString(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var json = path switch
            {
                "/admin/login" => """{"token":"token-1"}""",
                "/admin/models?gameId=01HRESOLVEDGAME000000000000" => """{"models":null}""",
                _ => """{"revision":1}""",
            };
            return new HttpResponseMessage(path == "/admin/models" ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}

#endif
