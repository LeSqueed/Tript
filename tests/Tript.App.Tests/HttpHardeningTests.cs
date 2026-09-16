// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Xunit;

namespace Tript.App.Tests;

public sealed class HttpHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tript-app-tests", "http-hardening", Guid.NewGuid().ToString("N"));
    private readonly string _webRoot;

    public HttpHardeningTests()
    {
        _webRoot = Path.Combine(_root, "dist");
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(_webRoot, "assets", "app.js"), "export {}");
        Directory.CreateDirectory(Path.Combine(_root, "dist-private"));
        File.WriteAllText(Path.Combine(_root, "dist-private", "secret.txt"), "secret");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("plain.mp4", "inline; filename=\"plain.mp4\"; filename*=UTF-8''plain.mp4")]
    [InlineData("clé.mp4", "inline; filename=\"cl_.mp4\"; filename*=UTF-8''cl%C3%A9.mp4")]
    [InlineData("say \"hi\".mp4", "inline; filename=\"say _hi_.mp4\"; filename*=UTF-8''say%20%22hi%22.mp4")]
    [InlineData("a\\b.mp4","inline; filename=\"a_b.mp4\"; filename*=UTF-8''a%5Cb.mp4")]
    public void ContentDisposition_IsAlwaysAValidAsciiHeader(string fileName, string expected)
    {
        var header = ContentServer.InlineDisposition(fileName);

        Assert.Equal(expected, header);
        Assert.All(header, character => Assert.InRange(character, ' ', '~'));
    }

    [Fact]
    public void UiFiles_ResolveInsideTheWebRoot()
    {
        Assert.Equal(Path.Combine(_webRoot, "index.html"), UiHost.ResolveFile(_webRoot, "/"));
        Assert.Equal(Path.Combine(_webRoot, "assets", "app.js"), UiHost.ResolveFile(_webRoot, "/assets/app.js"));
        Assert.Equal(Path.Combine(_webRoot, "index.html"), UiHost.ResolveFile(_webRoot, "/library"));
        Assert.Null(UiHost.ResolveFile(_webRoot, "/assets/missing.js"));
    }

    [Theory]
    [InlineData("/../dist-private/secret.txt")]
    [InlineData("/..%2Fdist-private/secret.txt")]
    public void UiFiles_NeverResolveOutsideTheWebRoot(string urlPath)
    {
        Assert.Null(UiHost.ResolveFile(_webRoot, urlPath));
    }

    [Fact]
    public void AnAbsolutePathInTheUrl_DoesNotEscapeTheWebRoot()
    {
        var outside = Path.Combine(_root, "dist-private", "secret.txt");
        var urlPath = "/" + outside.Replace(Path.DirectorySeparatorChar, '/');

        Assert.Null(UiHost.ResolveFile(_webRoot, urlPath));
    }

    [Fact]
    public void ASiblingFolderSharingTheWebRootsPrefix_IsNotInsideIt()
    {
        var sibling = Path.Combine(_root, "dist-private", "secret.txt");

        Assert.False(Tript.Core.FilePaths.IsUnder(sibling, _webRoot));
        Assert.Null(ContentServer.ResolveWithinRoot(_webRoot, "../dist-private/secret.txt"));
    }
}
