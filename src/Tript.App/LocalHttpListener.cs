// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;

namespace Tript.App;

internal abstract class LocalHttpListener : IDisposable
{
    private static readonly TimeSpan AcceptJoinTimeout = TimeSpan.FromSeconds(2);

    private readonly int _port;
    private readonly string _threadName;
    private readonly HttpListener _listener = new();
    private readonly Lock _startGate = new();
    private Thread? _acceptThread;
    private volatile bool _running;
    private int _disposed;

    protected LocalHttpListener(int port, string threadName)
    {
        _port = port;
        _threadName = threadName;
    }

    public void Start()
    {
        lock (_startGate)
        {
            if (_running)
                return;

            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            _running = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = _threadName,
            };
            _acceptThread.Start();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _running = false;
        OnStopping();
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

        _acceptThread?.Join(AcceptJoinTimeout);
        OnStopped();
    }

    protected abstract Task HandleAsync(HttpListenerContext context);

    protected virtual void OnStopping()
    {
    }

    protected virtual void OnStopped()
    {
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
}
