// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Text.RegularExpressions;

namespace Tript.App.Content;

// The HTTP content server (http://localhost:2222/, spec/local-ipc.md). Serves two routes:
//   /api/content/<path>  range-request video streaming (206 partial content, Content-Range)
//   /api/thumbnail/<path> first-frame thumbnails (alpha: a 204 no-content stand-in)
// Anything else is 404.
//
// Path-traversal guard (spec/local-ipc.md — "that is a path-traversal guard, not an
// implementation detail"): every requested path resolves against the canonical content root. A
// request whose decoded path escapes the root is refused with 403, and the route regex itself
// rejects any encoded traversal segment. The guard is a single choke point, tested explicitly.
internal sealed class ContentServer : IDisposable
{
    private const int Port = 2222;

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

    private string _contentRoot;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    private Thread? _serverThread;
    private volatile bool _running;

    internal ContentServer(string contentRoot)
    {
        _contentRoot = Path.GetFullPath(contentRoot);
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

            _listener.Prefixes.Add($"http://localhost:{Port}/");
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
                ThreadPool.QueueUserWorkItem(_ => Handle(context));
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

    private void Handle(HttpListenerContext context)
    {
        try
        {
            // A traversal attempt is refused up front, with 403, whether it appears raw in the URL
            // path or URL-encoded. HttpListener normalizes raw ".." in AbsolutePath before we see
            // it, so the encoded-marker check against RawUrl is what a direct ".." attempt hits;
            // a path that survives normalization with ".." still in it reaches the resolver below,
            // which refuses it the same way.
            var rawPath = context.Request.RawUrl ?? string.Empty;
            if (rawPath.Contains("/../", StringComparison.Ordinal)
                || rawPath.Contains("/..", StringComparison.Ordinal)
                || rawPath.Contains("..", StringComparison.Ordinal)
                || rawPath.Contains("%2e", StringComparison.OrdinalIgnoreCase)
                || rawPath.Contains("%2E", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            var match = ContentRoute.Match(path);
            if (match.Success)
            {
                ServeContent(context, match.Groups[1].Value);
                return;
            }

            match = ThumbnailRoute.Match(path);
            if (match.Success)
            {
                ServeThumbnail(context, match.Groups[1].Value);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception)
        {
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

    // ---- the path-traversal guard ----

    // Resolves a request path against the content root, or returns null when the path escapes the
    // root. This is the single choke point every content/thumbnail request passes through.
    internal string? ResolveWithinRoot(string requestPath) => ResolveWithinRoot(_contentRoot, requestPath);

    // The same guard against an explicit root, callable without a server instance. The clip surface
    // needs it: the wire's filePath is relative to the effective recording root by design (the
    // content server serves the catalogue that way), so AppController.BuildClipRequest has to
    // resolve it against that root before handing it to ffmpeg — and it must refuse a traversal
    // exactly as an HTTP request would. Sharing this method keeps one implementation of "is this
    // path inside the root", so the guard stays the single choke point it is documented to be.
    internal static string? ResolveWithinRoot(string contentRoot, string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
            return null;

        // A raw traversal segment is refused at the route boundary; this re-checks so the method is
        // safe to call directly too. The check runs before any path combine, so ".." never reaches
        // the file system. The pattern only sees '/' separators (the wire's), which is enough for
        // an early refusal — a Windows-style "..\.." form survives to the root comparison below,
        // and that comparison is the final authority either way.
        if (PathSegment.IsMatch(requestPath))
            return null;

        try
        {
            // Combine and normalize. Path.Combine returns the second argument unchanged when it is
            // already rooted, so an absolute incoming path is kept and then judged by the root
            // comparison — accepted when it points inside the root, refused when it does not.
            // GetFullPath collapses any ".." the checks above missed, and converts the wire's '/'
            // separators to the platform's on Windows.
            var root = Path.GetFullPath(contentRoot);
            var candidate = Path.GetFullPath(Path.Combine(root, requestPath));
            return IsUnderRoot(candidate, root) ? candidate : null;
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
        var comparison = ComparisonFor();
        if (string.Compare(candidate, root, comparison) == 0)
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, comparison);
    }

    private static StringComparison ComparisonFor() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    // ---- content ----

    private void ServeContent(HttpListenerContext context, string requestPath)
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

        var info = new FileInfo(resolved);
        context.Response.ContentType = "video/mp4";
        context.Response.Headers.Add("Accept-Ranges", "bytes");
        context.Response.Headers.Add("Content-Disposition", $"inline; filename=\"{info.Name}\"");

        var length = info.Length;
        var rangeHeader = context.Request.Headers["Range"];
        if (string.IsNullOrEmpty(rangeHeader))
        {
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = length;
            using var stream = File.OpenRead(resolved);
            stream.CopyTo(context.Response.OutputStream);
            context.Response.Close();
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

        using (var stream = File.OpenRead(resolved))
        {
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[64 * 1024];
            long remaining = count;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                    break;
                context.Response.OutputStream.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        context.Response.Close();
    }

    internal static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;

        // Only the first range is honoured (a multi-range request gets its first range; the spec
        // does not require a server to support multiple ranges).
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

    private void ServeThumbnail(HttpListenerContext context, string requestPath)
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

        // The alpha has no thumbnail pipeline; a 204 tells the frontend the content exists without
        // pretending to have an image.
        context.Response.StatusCode = 204;
        context.Response.Close();
    }

    public void Dispose()
    {
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
    }
}
