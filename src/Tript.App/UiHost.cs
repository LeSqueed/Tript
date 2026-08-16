// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;

namespace Tript.App;

// The UI host (http://localhost:2882/, spec/local-ipc.md). Serves the built frontend from the
// dist directory. The alpha ships no embedded manifest (release builds embed the UI; the dev-mode
// file server is an accepted alpha stand-in), so this is a plain static file server: an index
// fallback for the SPA routes, cache headers off, and a 404 for anything outside the web root.
internal sealed class UiHost : IDisposable
{
    private const int Port = 2882;

    private readonly string _webRoot;
    private readonly HttpListener _listener = new();

    private Thread? _serverThread;
    private volatile bool _running;

    internal UiHost(string webRoot)
    {
        _webRoot = Path.GetFullPath(webRoot);
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
                Name = "Tript.App.Ui.Accept",
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
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/")
                path = "/index.html";

            // A simple traversal guard, same discipline as the content server: nothing outside the
            // web root is ever served, and ".." never reaches the file system.
            var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (relative.Contains("..", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var candidate = Path.GetFullPath(Path.Combine(_webRoot, relative));
            if (!candidate.StartsWith(_webRoot, ComparisonFor()))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            if (!File.Exists(candidate))
            {
                // SPA fallback: an unknown route serves the shell, which decides what to render.
                candidate = Path.Combine(_webRoot, "index.html");
                if (!File.Exists(candidate))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
            }

            context.Response.ContentType = ContentTypeFor(candidate);
            context.Response.ContentLength64 = new FileInfo(candidate).Length;
            using var stream = File.OpenRead(candidate);
            stream.CopyTo(context.Response.OutputStream);
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

    private static string ContentTypeFor(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".html" => "text/html",
            ".js" => "application/javascript",
            ".css" => "text/css",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico" => "image/x-icon",
            ".map" => "application/json",
            _ => "application/octet-stream",
        };
    }

    private static StringComparison ComparisonFor() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public void Dispose()
    {
        _running = false;
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
