using System.Text.Json;

namespace Tript.App.Resolver;

internal sealed record ResolverConfig(Uri BaseUri, string? ApiKey)
{
    internal const string FileName = "resolver.json";

    internal static ResolverConfig? FromFile(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path))
            return null;

        ResolverFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ResolverFile>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"{path} must contain valid JSON.", exception);
        }

        if (file is null || string.IsNullOrWhiteSpace(file.Url))
            throw new InvalidOperationException($"{path} must contain a resolver URL.");
        var value = file.Url.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{path} must contain an absolute HTTP or HTTPS resolver URL.");
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var apiKey = file.ApiKey?.Trim();
        return new ResolverConfig(builder.Uri, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
    }

    internal Uri Endpoint(string relativePath) => new(BaseUri, relativePath.TrimStart('/'));

    private sealed class ResolverFile
    {
        public string? Url { get; init; }

        public string? ApiKey { get; init; }
    }
}
