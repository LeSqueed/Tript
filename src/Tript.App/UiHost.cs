// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;

namespace Tript.App;

// The UI host. Serves the built frontend from the dist
// directory.
internal sealed class UiHost : IDisposable
{
    private const int Port = LocalPorts.Ui;

    private readonly string _webRoot;
    private readonly SessionToken _token;
    private readonly HttpListener _listener = new();

    private Thread? _serverThread;
    private volatile bool _running;

    internal UiHost(string webRoot, SessionToken token)
    {
        _webRoot = Path.GetFullPath(webRoot);
        _token = token;
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
            // Gated like the other two listeners, and for the sharpest reason: this host serves the
            // SPA, so an ungated UI host hands the frontend — and with it the app's whole behaviour
            // — to anything that asks for the page.
            //
            // The document request carries ?k=; on success it is answered with a cookie, so the
            // assets it pulls in need no token in their URLs. Nothing is served either way without
            // one, and the refusal never repeats what was presented.
            if (!_token.Authorises(context.Request, acceptCookie: true))
            {
                Refuse(context);
                return;
            }

            var setCookie = context.Request.QueryString[SessionToken.QueryKey] is not null;

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

            if (setCookie)
            {
                // HttpOnly so no script can read it back out, SameSite=Strict so another site's
                // navigation never carries it, and no Max-Age so it dies with the browser session —
                // the token is per launch and must not outlive one.
                context.Response.AppendHeader("Set-Cookie",
                    $"{SessionToken.CookieName}={_token.Value}; Path=/; HttpOnly; SameSite=Strict");
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

    // A short plain-text refusal, never the SPA and never an echo of what was presented.
    private static void Refuse(HttpListenerContext context)
    {
        var body = "Tript: this page is served only to the session that launched the app.\n"u8.ToArray();
        context.Response.StatusCode = 403;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.Close();
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
