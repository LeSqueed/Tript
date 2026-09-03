// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Buffers;
using System.Net;
using System.Text.RegularExpressions;

using Tript.App;
using Tript.Core;

namespace Tript.App.Content;

// The HTTP content server. Serves two routes: /api/content/<path>  range-request video streaming
// (206 partial content, Content-Range) /api/thumbnail/<path> a cached still frame from the video,
// as JPEG (204 when there is none) Anything else is 404.
internal sealed class ContentServer : IDisposable
{
    private readonly int _port;

    // The route regexes only ever capture the path after /api/content/ or /api/thumbnail/. They
    // accept the raw path including ".." segments: the resolver below is the guard's single choke
    // point and the thing the path-traversal test asserts against, so a traversal must reach it
    // and be refused with 403 there rather than silently 404'd at the route boundary.
    private static readonly Regex ContentRoute =
        new(@"^/api/content/(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ThumbnailRoute =
        new(@"^/api/thumbnail/(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Matches a traversal segment anywhere in the path. Used by the resolver, which refuses a path
    // containing one before it ever reaches the file system.
    private static readonly Regex PathSegment =
        new(@"(^|/)\.\.(/|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Written on the IPC thread by UpdateRoot, read on every accept-loop worker: volatile so a
    // worker cannot keep serving from the old root after the recording directory moved.
    private volatile string _contentRoot;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    // Captured once: request handlers outlive Dispose by a moment, and reading _cts.Token after
    // the source is disposed throws.
    private readonly CancellationToken _shutdown;

    private const int StreamBufferSize = 64 * 1024;

    // The thumbnail cache the /api/thumbnail route serves from. Optional: a server built without one
    // answers every thumbnail request with 204, which is the same answer the frontend already
    // handles for a video no frame could be taken from.
    private readonly ThumbnailStore? _thumbnails;

    // The per-launch session token. This listener checks no Origin by design — a <video> element
    // sends none — so before the token any page that guessed a path could embed and play a
    // recording. The token has to ride in the query string for the same reason: a media element can
    // carry nothing but a URL.
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

    // Switches the guard root to a new directory. A settings change that moves the recording
    // output directory rebuilds the root the traversal guard resolves against; the listener stays
    // up and keeps serving from the new root.
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
            // A traversal attempt is refused up front, with 403, whether it appears raw in the URL
            // path or URL-encoded. HttpListener normalizes raw ".." in AbsolutePath before we see
            // it, so the encoded-marker check against RawUrl is what a direct ".." attempt hits;
            // a path that survives normalization with ".." still in it reaches the resolver below,
            // which refuses it the same way. Anchored on a separator, deliberately: a bare
            // "contains .." also refused every legitimate name with two dots in it — "my..clip.mp4"
            // was a 403 — and a traversal segment under /api/content/ always follows a separator.
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

            // After the raw-URL guard, deliberately: the guard reads RawUrl before anything is
            // decoded, and nothing here may run ahead of it. The token is read from the parsed
            // query string, which cannot reach the path the guard inspects.
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
            // Every other catch in this file logs; without this a disk error or a bug in ServeContent
            // leaves no trace anywhere and the video simply fails to play.
            Console.Error.WriteLine($"Tript.App.Content: request failed: {exception}");
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

    // AbsolutePath keeps the escapes, so a recording called "my clip.mp4" arrived as "my%20clip.mp4"
    // and was looked up under that literal name — a 404 for every file with a space, a '#' or a '?'
    // in it. Decoded HERE and nowhere earlier: the raw-URL guard above refuses "%2e" before this
    // runs, and decoding first would hand it a traversal it can no longer see.
    private static string Decode(string routePath)
    {
        try
        {
            return Uri.UnescapeDataString(routePath);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            // A malformed escape is not a path; the resolver refuses it the same as any other.
            return routePath;
        }
    }

    // ---- the path-traversal guard ----

    // Resolves a request path against the content root, or returns null when the path escapes the
    // root. This is the single choke point every content/thumbnail request passes through.
    internal string? ResolveWithinRoot(string requestPath) => ResolveWithinRoot(_contentRoot, requestPath);

    // The same guard against an explicit root, callable without a server instance. The clip surface
    // needs it: the wire's filePath is relative to the effective recording root by design (the
    // content server serves the catalogue that way), so AppController.BuildClipRequest has to
    // resolve it against that root before handing it to ffmpeg — and it must refuse a traversal
    // exactly as an HTTP request would. allowTrash is for the bin's own resolution against the
    // trash root; every other caller — the HTTP routes, the clip surface, the delete path —
    // resolves against the recording root, where a path into .trash/ is refused: trashed content is
    // deleted content and must not be served, clipped or listed.
    internal static string? ResolveWithinRoot(string contentRoot, string requestPath, bool allowTrash = false)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
            return null;

        // A raw traversal segment is refused at the route boundary; this re-checks so the method is
        // safe to call directly too. The check runs before any path combine, so ".." never reaches
        // the file system.
        if (PathSegment.IsMatch(requestPath))
            return null;

        try
        {
            // Combine and normalize. Path.Combine returns the second argument unchanged when it is
            // already rooted, so an absolute incoming path is kept and then judged by the root
            // comparison — accepted when it points inside the root, refused when it does not.
            var root = Path.GetFullPath(contentRoot);
            var candidate = Path.GetFullPath(Path.Combine(root, requestPath));
            if (!IsUnderRoot(candidate, root))
                return null;
            return allowTrash || !IsInTrash(candidate, root) ? candidate : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                             or PathTooLongException)
        {
            // A path the platform cannot even express (an embedded NUL, a reserved device name) is
            // refused rather than thrown: to every caller it is simply not a path inside the root.
            return null;
        }
    }

    // Whether a resolved path is the root itself or a path below it. The directory separator
    // suffix guards the classic prefix trap: "/content-root-other" must not pass for
    // "/content-root".
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

    // Whether a resolved path sits in the recycle bin at the top of the root.
    private static bool IsInTrash(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var first = separator >= 0 ? relative[..separator] : relative;
        return first.Equals(TrashStore.DirectoryName, FilePaths.Comparison);
    }

    // ---- content ----

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

        // The length is taken from the open handle, not from a FileInfo snapshot, and every copy
        // below is bounded by it. A session still being recorded grows between the two, so a
        // snapshot length meant declaring one Content-Length and then writing more bytes than that
        // — a protocol violation the client sees as a corrupt or truncated video.
        // Open with ReadWrite|Delete sharing: a recording that is actively being written holds the
        // file open for write, and File.OpenRead (FileShare.Read) cannot open alongside it on
        // Windows — a sharing violation, so a 500 for a session that is mid-record. Linux has no
        // sharing model, so the open there was always fine and the mode still matches.
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

    // Copies exactly `count` bytes and closes the response. A file that was truncated underneath us
    // cannot supply them; the connection is aborted rather than closed, because closing short of the
    // declared Content-Length leaves the client waiting for bytes that will never arrive.
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

        // Only the first range is honoured; RFC 7233 does not require a server to support multiple
        // ranges.
        var match = Regex.Match(header, @"bytes=(\d*)-(\d*)", RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var first = match.Groups[1].Value;
        var last = match.Groups[2].Value;

        if (first.Length == 0)
        {
            // A suffix range: the final N bytes.
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

    // ---- thumbnail ----

    // A still frame from the video, as JPEG, cached on disk (ThumbnailStore). The status contract
    // is deliberately two-valued for the frontend: 200 with an image, or 204 meaning "draw the
    // placeholder card".
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
            // The store contains its own queue failures; this catches the read of the cached file
            // (deleted between lookup and the read, permissions changed underneath) so that no
            // thumbnail request can ever produce a 500 or an unhandled exception on a worker thread.
            // Cancellation is left to the caller, which aborts the response instead of answering it.
            Console.Error.WriteLine($"Tript.App: could not serve a thumbnail for '{resolved}': {exception.Message}");
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
        // The webview re-mounts the grid on every navigation, so without a cache header each visit
        // re-fetches every card. An hour is long enough to make scrolling and route changes free
        // and short enough that a video replaced in place under the same name (the only way a
        // thumbnail changes) is picked up in the same session.
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
            // See IpcServer.Dispose: a Close racing the listener's teardown can throw; the process
            // is exiting.
        }

        // Handlers still in flight hold the captured token, so they cannot trip over the disposed
        // source; the accept thread is the only thing left to wait for.
        _serverThread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
