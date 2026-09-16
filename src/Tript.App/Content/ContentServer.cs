// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Buffers;
using System.Net;
using System.Text.RegularExpressions;

using Serilog;
using Tript.App;
using Tript.Core;

namespace Tript.App.Content;

internal sealed class ContentServer : IDisposable
{
    private readonly int _port;

    private static readonly Regex ContentRoute =
        new(@"^/api/content/(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ThumbnailRoute =
        new(@"^/api/thumbnail/(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PathSegment =
        new(@"(^|/)\.\.(/|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private volatile string _contentRoot;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly CancellationToken _shutdown;

    private const int StreamBufferSize = 64 * 1024;

    private readonly ThumbnailStore? _thumbnails;

    private readonly SessionToken _token;

    private Thread? _serverThread;
    private volatile bool _running;
    private int _disposed;

    internal ContentServer(string contentRoot, SessionToken token, ThumbnailStore? thumbnails = null,
        int port = LocalPorts.Content)
    {
        _contentRoot = Path.GetFullPath(contentRoot);
        _token = token;
        _thumbnails = thumbnails;
        _port = port;
        _shutdown = _cts.Token;
    }

    internal string ContentRoot => _contentRoot;

    internal void UpdateRoot(string contentRoot)
    {
        _contentRoot = Path.GetFullPath(contentRoot);
    }

    public void Start()
    {
        lock (_listener)
        {
            if (_running)
                return;

            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            _running = true;

            _serverThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "Tript.App.Content.Accept",
            };
            _serverThread.Start();
        }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var context = _listener.GetContext();
                ThreadPool.QueueUserWorkItem(_ => _ = HandleAsync(context));
            }
            catch (HttpListenerException)
            {
                if (_running)
                    continue;
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var rawPath = context.Request.RawUrl ?? string.Empty;
            if (rawPath.Contains("/../", StringComparison.Ordinal)
                || rawPath.Contains("/..", StringComparison.Ordinal)
                || rawPath.Contains("%2e", StringComparison.OrdinalIgnoreCase)
                || rawPath.Contains("%2E", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            if (!_token.Authorises(context.Request))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            var match = ContentRoute.Match(path);
            if (match.Success)
            {
                await ServeContentAsync(context, Decode(match.Groups[1].Value), _shutdown);
                return;
            }

            match = ThumbnailRoute.Match(path);
            if (match.Success)
            {
                await ServeThumbnailAsync(context, Decode(match.Groups[1].Value), _shutdown);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            try { context.Response.Abort(); } catch { }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Content: a request failed");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private static string Decode(string routePath)
    {
        try
        {
            return Uri.UnescapeDataString(routePath);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            return routePath;
        }
    }

    internal string? ResolveWithinRoot(string requestPath) => ResolveWithinRoot(_contentRoot, requestPath);

    internal static string? ResolveWithinRoot(string contentRoot, string requestPath, bool allowTrash = false)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
            return null;

        if (PathSegment.IsMatch(requestPath))
            return null;

        try
        {
            var root = Path.GetFullPath(contentRoot);
            var candidate = Path.GetFullPath(Path.Combine(root, requestPath));
            if (!IsUnderRoot(candidate, root))
                return null;
            return allowTrash || !IsInTrash(candidate, root) ? candidate : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                             or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsUnderRoot(string candidate, string root)
    {
        var comparison = FilePaths.Comparison;
        if (string.Compare(candidate, root, comparison) == 0)
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, comparison);
    }

    private static bool IsInTrash(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var first = separator >= 0 ? relative[..separator] : relative;
        return first.Equals(TrashStore.DirectoryName, FilePaths.Comparison);
    }

    private async Task ServeContentAsync(HttpListenerContext context, string requestPath,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveWithinRoot(requestPath);
        if (resolved is null)
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        if (!File.Exists(resolved))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        await using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;

        context.Response.ContentType = "video/mp4";
        context.Response.Headers.Add("Accept-Ranges", "bytes");
        context.Response.Headers.Add("Content-Disposition", $"inline; filename=\"{Path.GetFileName(resolved)}\"");

        var rangeHeader = context.Request.Headers["Range"];
        if (string.IsNullOrEmpty(rangeHeader))
        {
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = length;
            await WriteExactlyAsync(context, stream, length, cancellationToken);
            return;
        }

        if (!TryParseRange(rangeHeader, length, out var start, out var end))
        {
            context.Response.StatusCode = 416;
            context.Response.ContentLength64 = 0;
            context.Response.Close();
            return;
        }

        var count = end - start + 1;
        context.Response.StatusCode = 206;
        context.Response.ContentLength64 = count;
        context.Response.Headers.Add("Content-Range", $"bytes {start}-{end}/{length}");

        stream.Seek(start, SeekOrigin.Begin);
        await WriteExactlyAsync(context, stream, count, cancellationToken);
    }

    private static async Task WriteExactlyAsync(HttpListenerContext context, Stream stream, long count,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            var remaining = count;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                {
                    context.Response.Abort();
                    return;
                }

                await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }

            context.Response.Close();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;

        var match = Regex.Match(header, @"bytes=(\d*)-(\d*)", RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var first = match.Groups[1].Value;
        var last = match.Groups[2].Value;

        if (first.Length == 0)
        {
            if (last.Length == 0 || !long.TryParse(last, out var suffix) || suffix <= 0)
                return false;
            if (suffix > length)
                suffix = length;
            start = length - suffix;
            end = length - 1;
        }
        else
        {
            if (!long.TryParse(first, out start))
                return false;
            if (last.Length > 0)
            {
                if (!long.TryParse(last, out end))
                    return false;
            }

            if (start >= length)
                return false;
            if (end >= length)
                end = length - 1;
            if (end < start)
                return false;
        }

        return true;
    }

    private async Task ServeThumbnailAsync(HttpListenerContext context, string requestPath,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveWithinRoot(requestPath);
        if (resolved is null)
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        byte[]? image = null;
        try
        {
            if (File.Exists(resolved))
            {
                var cached = _thumbnails?.GetOrQueue(resolved);
                if (cached is not null)
                    image = await File.ReadAllBytesAsync(cached, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Warning("Content: could not serve a thumbnail for {Path}: {Reason}", resolved, exception.Message);
            image = null;
        }

        if (image is null || image.Length == 0)
        {
            context.Response.StatusCode = 204;
            context.Response.Close();
            return;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/jpeg";
        context.Response.ContentLength64 = image.Length;

        context.Response.Headers.Add("Cache-Control", "private, max-age=3600");
        context.Response.Headers.Add("Last-Modified",
            File.GetLastWriteTimeUtc(resolved).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        await context.Response.OutputStream.WriteAsync(image, cancellationToken);
        context.Response.Close();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _running = false;
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        try
        {
            _listener.Close();
        }
        catch
        {
        }

        _serverThread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
