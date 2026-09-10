// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using Tript.Core;

namespace Tript.App;

internal sealed class UiHost : IDisposable
{
    private readonly int _port;

    private readonly string _webRoot;
    private readonly SessionToken _token;
    private readonly HttpListener _listener = new();

    private Thread? _serverThread;
    private volatile bool _running;

    internal UiHost(string webRoot, SessionToken token, int port = LocalPorts.Ui)
    {
        _webRoot = Path.GetFullPath(webRoot);
        _token = token;
        _port = port;
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
            if (!_token.AuthorisesUi(context.Request))
            {
                Refuse(context);
                return;
            }

            var setCookie = context.Request.QueryString[SessionToken.QueryKey] is not null;

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/")
                path = "/index.html";

            var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (relative.Contains("..", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var candidate = Path.GetFullPath(Path.Combine(_webRoot, relative));
            if (!candidate.StartsWith(_webRoot, FilePaths.Comparison))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            if (!File.Exists(candidate))
            {
                if (Path.HasExtension(relative))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

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
        }
    }
}
