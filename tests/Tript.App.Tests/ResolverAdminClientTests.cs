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

    private const string OcrEvents =
        """[{"id":1,"name":"Victory","type":"Trigger","detectionKind":"Ocr","classId":-1,"bookmarkType":"Play"}]""";
    private const string ObjectEvents =
        """[{"id":1,"name":"Elimination","type":"Trigger","detectionKind":"Object","classId":0,"bookmarkType":"Kill"}]""";

    private (AdminHandler Handler, HttpClient Http, ResolverAdminClient Client) NewClient()
    {
        var handler = new AdminHandler();
        var http = new HttpClient(handler);
        return (handler, http, new ResolverAdminClient(new ResolverConfig(new Uri("https://resolver.test/"), null), http));
    }

    [Fact]
    public async Task Publish_AnOcrOnlyModel_UploadsItsOcrFilesAndTheMinimumAppVersion()
    {
        Directory.CreateDirectory(_root);
        var events = Path.Combine(_root, "events.json");
        File.WriteAllText(events, OcrEvents);
        File.WriteAllBytes(Path.Combine(_root, "ocr_model.onnx"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(_root, "ocr_dict.txt"), "A\nB\n");
        var (handler, http, client) = NewClient();
        using (http)
        using (client)
        {
            var revision = await client.PublishAsync("01HRESOLVEDGAME000000000000", _root, events,
                "admin", "password-123", minimumAppVersion: "1.2.1");

            Assert.Equal(1, revision);
            Assert.Contains("name=minimumAppVersion", handler.UploadBody);
            Assert.Contains("1.2.1", handler.UploadBody);
            Assert.Contains("ocr_model.onnx", handler.UploadBody);
            Assert.Contains("ocr_dict.txt", handler.UploadBody);
        }
    }

    [Fact]
    public async Task Publish_OcrEventsWithoutAnOcrModel_IsRefusedBeforeAnyRequest()
    {
        Directory.CreateDirectory(_root);
        var events = Path.Combine(_root, "events.json");
        File.WriteAllText(events, OcrEvents);
        var (handler, http, client) = NewClient();
        using (http)
        using (client)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PublishAsync(
                "01HRESOLVEDGAME000000000000", _root, events, "admin", "password-123"));

            Assert.Contains("OCR", error.Message);
            Assert.Empty(handler.Paths);
        }
    }

    [Fact]
    public async Task Publish_ObjectEventsWithoutAnObjectModel_IsRefusedBeforeAnyRequest()
    {
        Directory.CreateDirectory(_root);
        var events = Path.Combine(_root, "events.json");
        File.WriteAllText(events, ObjectEvents);
        var (handler, http, client) = NewClient();
        using (http)
        using (client)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PublishAsync(
                "01HRESOLVEDGAME000000000000", _root, events, "admin", "password-123"));

            Assert.Contains("model.onnx", error.Message);
            Assert.Empty(handler.Paths);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("1.2.1", "1.2.1")]
    [InlineData(" 1.2.1 ", "1.2.1")]
    [InlineData("01.2.3", "1.2.3")]
    public void MinimumAppVersion_IsOptionalAndNormalized(string? input, string? expected)
    {
        Assert.Equal(expected, ResolverAdminClient.NormalizeMinimumAppVersion(input));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.1.0")]
    [InlineData("v1.2.1")]
    [InlineData("1.2.x")]
    public void MinimumAppVersion_RejectsAnythingButMajorMinorPatch(string input)
    {
        Assert.Throws<InvalidOperationException>(() => ResolverAdminClient.NormalizeMinimumAppVersion(input));
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
