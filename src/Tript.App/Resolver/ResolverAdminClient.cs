#if TRIPT_TRAINING

using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Tript.App.Models;
using Tript.Detection;

namespace Tript.App.Resolver;

internal sealed class ResolverAdminClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(Wire.Options) { PropertyNameCaseInsensitive = true };
    private readonly ResolverConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    internal ResolverAdminClient(ResolverConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsHttp = httpClient is null;
    }

    internal async Task<int> PublishAsync(string gameId, string modelPath, string eventsPath,
        string username, string password, CancellationToken cancellationToken = default)
    {
        var token = await LoginAsync(username, password, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var revision = await NextRevisionAsync(gameId, token, cancellationToken).ConfigureAwait(false);
            var archivePath = BuildArchive(gameId, revision, modelPath, eventsPath);
            try
            {
                var published = await UploadAsync(gameId, archivePath, token, cancellationToken)
                    .ConfigureAwait(false);
                if (published == revision) return published;
                await DeleteAsync(gameId, published, token, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(archivePath);
            }
        }
        throw new InvalidOperationException("The resolver model revision changed repeatedly during publication.");
    }

    private async Task<string> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(_config.Endpoint("admin/login"),
            new { username, password }, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "The resolver rejected the admin credentials."
                : "The resolver admin login failed.");
        var login = await response.Content.ReadFromJsonAsync<AdminLoginResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(login?.Token)
            ? login.Token
            : throw new InvalidDataException("The resolver returned no admin session token.");
    }

    private async Task<int> NextRevisionAsync(string gameId, string token, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get,
            _config.Endpoint($"admin/models?gameId={Uri.EscapeDataString(gameId)}"), token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<AdminModelList>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        var models = list?.Models ?? [];
        return (models.Count > 0 ? models.Max(model => model.Revision) : 0) + 1;
    }

    private async Task<int> UploadAsync(string gameId, string archivePath, string token,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(gameId), "gameId");
        form.Add(new StringContent(ModelApiV1Compatibility.Version.ToString(
            System.Globalization.CultureInfo.InvariantCulture)), "modelApiVersion");
        await using var stream = File.OpenRead(archivePath);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "model.zip");
        using var request = Authorized(HttpMethod.Post, _config.Endpoint("admin/models"), token);
        request.Content = form;
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var published = await response.Content.ReadFromJsonAsync<AdminModel>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return published?.Revision > 0
            ? published.Revision
            : throw new InvalidDataException("The resolver returned no published model revision.");
    }

    private async Task DeleteAsync(string gameId, int revision, string token,
        CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Delete,
            _config.Endpoint($"admin/models/{Uri.EscapeDataString(gameId)}/{revision}"), token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string BuildArchive(string gameId, int revision, string modelPath, string eventsPath)
    {
        if (!File.Exists(modelPath) || !File.Exists(eventsPath))
            throw new FileNotFoundException("The installed model is incomplete.");
        var archivePath = Path.Combine(Path.GetTempPath(), $"tript-model-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(modelPath, "model.onnx", CompressionLevel.NoCompression);
        archive.CreateEntryFromFile(eventsPath, "events.json", CompressionLevel.Optimal);
        var metadata = new GameModelPackageMetadata
        {
            PackageFormatVersion = 1,
            GameId = gameId,
            ModelApiVersion = ModelApiV1Compatibility.Version,
            Revision = revision,
            Files = new Dictionary<string, GameModelPackageFile>(StringComparer.Ordinal)
            {
                ["model.onnx"] = PackageFile(modelPath),
                ["events.json"] = PackageFile(eventsPath),
            },
        };
        var entry = archive.CreateEntry("package.json", CompressionLevel.Optimal);
        using var destination = entry.Open();
        JsonSerializer.Serialize(destination, metadata, JsonOptions);
        return archivePath;
    }

    private static GameModelPackageFile PackageFile(string path) => new()
    {
        SizeBytes = new FileInfo(path).Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
    };

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private sealed class AdminLoginResponse { public string Token { get; init; } = string.Empty; }
    private sealed class AdminModelList { public List<AdminModel>? Models { get; init; } }
    private sealed class AdminModel { public int Revision { get; init; } }
}

#endif
