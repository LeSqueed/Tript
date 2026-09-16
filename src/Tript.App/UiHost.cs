// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using Serilog;
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

            var candidate = ResolveFile(_webRoot, context.Request.Url?.AbsolutePath ?? "/");
            if (candidate is null)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            if (setCookie)
            {
                context.Response.AppendHeader("Set-Cookie",
                    $"{SessionToken.CookieName}={_token.Value}; Path=/; HttpOnly; SameSite=Strict");
            }

            AddSecurityHeaders(context.Response);
            if (IsIndexDocument(candidate))
                context.Response.AddHeader("Cache-Control", "no-cache");
            context.Response.ContentType = ContentTypeFor(candidate);
            context.Response.ContentLength64 = new FileInfo(candidate).Length;
            using var stream = File.OpenRead(candidate);
            stream.CopyTo(context.Response.OutputStream);
            context.Response.Close();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Ui: a request failed");
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

    internal static string? ResolveFile(string webRoot, string urlPath)
    {
        var path = urlPath == "/" ? "/index.html" : urlPath;
        var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Contains("..", StringComparison.Ordinal))
            return null;

        var candidate = Path.GetFullPath(Path.Combine(webRoot, relative));
        if (!FilePaths.IsUnder(candidate, webRoot))
            return null;
        if (File.Exists(candidate))
            return candidate;
        if (Path.HasExtension(relative))
            return null;

        var index = Path.Combine(webRoot, "index.html");
        return File.Exists(index) ? index : null;
    }

    private static void Refuse(HttpListenerContext context)
    {
        var body = "Tript: this page is served only to the session that launched the app.\n"u8.ToArray();
        AddSecurityHeaders(context.Response);
        context.Response.StatusCode = 403;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.Close();
    }

    internal static void AddSecurityHeaders(HttpListenerResponse response)
    {
        response.AddHeader("X-Content-Type-Options", "nosniff");
        response.AddHeader("Referrer-Policy", "same-origin");
        response.AddHeader("X-Frame-Options", "DENY");
    }

    private bool IsIndexDocument(string path) =>
        string.Equals(path, Path.Combine(_webRoot, "index.html"), FilePaths.Comparison);

    internal static string ContentTypeFor(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
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
